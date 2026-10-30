package app.caretreader.tts

import android.accessibilityservice.AccessibilityService
import android.os.Build
import android.view.accessibility.AccessibilityNodeInfo
import android.view.accessibility.AccessibilityWindowInfo
import java.util.ArrayDeque

/**
 * 取要朗读的文字（对应 Windows 版 CaretTextGrabber）。
 *
 * 顺序：
 *  1. 当前输入框（有光标的地方）：有选中 → 只读选中；没选中 → 从光标处读到结尾；
 *  2. 屏幕上任何“有选中范围”的文字（可选择文字的阅读页、部分浏览器）→ 只读选中；
 *  3. 读屏焦点（TalkBack 正在指的那一项）→ 读它的文字。
 * 密码框一律不读。只记录方式和字数，不记录文字本身。
 */
object TextGrab {
    class Result(val text: String, val label: String, val method: String)
    class Remembered(val pkg: String, val text: CharSequence?, val from: Int, val to: Int, val node: AccessibilityNodeInfo?, val atMs: Long) {
        /** 按开始时才截取选中的那一段（最多 10 万字）。 */
        fun selected(): String? {
            val full = (text ?: try { node?.text } catch (_: Throwable) { null })?.toString() ?: return null
            if (to > full.length) return null
            return full.substring(from, minOf(to, from + 100_000)).takeIf { it.isNotBlank() }
        }
    }
    @Volatile var lastSelection: Remembered? = null

    fun grab(svc: AccessibilityService): Result? {
        val own = svc.packageName
        val root = svc.rootInActiveWindow
        // 1. 输入焦点
        val focus = try { root?.findFocus(AccessibilityNodeInfo.FOCUS_INPUT) } catch (_: Throwable) { null }
        if (focus != null && focus.packageName?.toString() != own) {
            fromEditable(focus)?.let { return it }
        }
        // 2. 任何窗口里有选中范围的文字
        val roots = ArrayList<AccessibilityNodeInfo>()
        try {
            // 注意：不能用 java List.forEach（API 24 才有，lint NewApi），用 for 循环
            val ws = svc.windows
            // 审查采纳：跳过输入法、状态栏/导航栏和我们自己的悬浮条
            val skip = setOf(AccessibilityWindowInfo.TYPE_ACCESSIBILITY_OVERLAY, AccessibilityWindowInfo.TYPE_INPUT_METHOD, AccessibilityWindowInfo.TYPE_SYSTEM)
            if (ws != null) for (w in ws) if (w.type !in skip) w.root?.let { roots.add(it) }
        } catch (_: Throwable) {}
        if (roots.isEmpty() && root != null) roots.add(root)
        for (r in roots) {
            if (r.packageName?.toString() == own) continue
            findSelection(r)?.let { return it }
        }
        // 2b. 刚才在这个 App 里选中过文字（选中事件记下的，2 分钟内有效）
        lastSelection?.let { m ->
            val active = root?.packageName?.toString()
            if (android.os.SystemClock.uptimeMillis() - m.atMs < 120_000 && (active == null || active == m.pkg))
                m.selected()?.let { return Result(it, "朗读选中", "selection-event") }
        }
        // 3. 读屏焦点
        val a11y = try { root?.findFocus(AccessibilityNodeInfo.FOCUS_ACCESSIBILITY) } catch (_: Throwable) { null }
        if (a11y != null && !a11y.isPassword) {
            val t = nodeText(a11y)
            if (!t.isNullOrBlank()) return Result(t, "朗读读屏焦点", "a11y-focus")
        }
        return null
    }

    private fun showingHint(n: AccessibilityNodeInfo) = Build.VERSION.SDK_INT >= 26 && n.isShowingHintText

    private fun nodeText(n: AccessibilityNodeInfo): String? {
        val t = n.text?.toString()
        if (!t.isNullOrBlank() && !showingHint(n)) return t
        return n.contentDescription?.toString()
    }

    private fun fromEditable(n: AccessibilityNodeInfo): Result? {
        if (n.isPassword) return Result("", "密码框不朗读", "password")
        val text = n.text?.toString() ?: return null
        if (text.isEmpty() || showingHint(n)) return null
        var s = n.textSelectionStart; var e = n.textSelectionEnd
        if (s > e) { val x = s; s = e; e = x }
        s = s.coerceIn(-1, text.length); e = e.coerceIn(-1, text.length)
        return when {
            s >= 0 && e > s && text.substring(s, e).isNotBlank() -> Result(text.substring(s, e), "朗读选中", "input-selection")
            s >= 0 && s < text.length && text.substring(s).isNotBlank() -> Result(text.substring(s), "从光标处朗读", "input-caret")
            s >= 0 -> Result("", "光标后面没有文字", "input-caret-end")
            else -> Result(text, "朗读输入框", "input-all")
        }
    }

    /** 广度优先找第一个有选中范围的节点（最多看 4000 个，防止超大页面卡住）。 */
    private fun findSelection(root: AccessibilityNodeInfo): Result? {
        val q = ArrayDeque<AccessibilityNodeInfo>()
        q.add(root)
        var seen = 0
        while (q.isNotEmpty() && seen < 4000) {
            val n = q.poll() ?: break
            seen++
            val s = n.textSelectionStart; val e = n.textSelectionEnd
            if (s >= 0 && e >= 0 && s != e && !n.isPassword) {
                val t = n.text?.toString()
                if (t != null) {
                    val a = minOf(s, e).coerceIn(0, t.length); val b = maxOf(s, e).coerceIn(0, t.length)
                    if (b > a && t.substring(a, b).isNotBlank()) return Result(t.substring(a, b), "朗读选中", "selection")
                }
            }
            for (i in 0 until n.childCount) try { n.getChild(i)?.let { q.add(it) } } catch (_: Throwable) {}
        }
        return null
    }

    /** 和 Windows 版一样清理控制字符：\v \a \f → 换行；U+FFFC（图片占位）和其它控制字符去掉。 */
    fun clean(s: String): String {
        val sb = StringBuilder(s.length)
        for (c in s) {
            when {
                c == '\u000B' || c == '\u0007' || c == '\u000C' || c == '\u2028' || c == '\u2029' -> sb.append('\n')
                c == '\uFFFC' -> {}
                c == '\n' || c == '\r' || c == '\t' -> sb.append(c)
                Character.isISOControl(c) -> {}
                else -> sb.append(c)
            }
        }
        return sb.toString()
    }
}
