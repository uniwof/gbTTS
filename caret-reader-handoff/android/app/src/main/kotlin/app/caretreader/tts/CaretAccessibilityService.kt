package app.caretreader.tts

import android.accessibilityservice.AccessibilityButtonController
import android.accessibilityservice.AccessibilityService
import android.annotation.SuppressLint
import android.content.Intent
import android.graphics.Color
import android.graphics.PixelFormat
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.util.Log
import android.util.TypedValue
import android.view.Gravity
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.View
import android.view.WindowManager
import android.view.accessibility.AccessibilityEvent
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import kotlin.math.abs
import kotlin.math.max
import kotlin.math.roundToInt

/**
 * 无障碍服务（Android 上代替 Windows 的 UI Automation + 全局快捷键 + 置顶小窗口）：
 *  - 悬浮朗读条：开始/暂停/继续、停止、设置；左边黑条可以拖动；点它不会抢走输入框的光标和选中。
 *  - 实体键盘快捷键：默认 Ctrl+Alt+R（开始/暂停/继续）、Ctrl+Alt+S（停止），可在设置里改。
 *  - 系统“无障碍按钮/手势”（Android 8+）：点一下 = 开始/暂停/继续。
 *  - 取文字：有选中读选中，没有就从光标处读到结尾（见 TextGrab）。
 */
class CaretAccessibilityService : AccessibilityService() {

    private val INK = Color.rgb(0x11, 0x11, 0x11)
    private val GREY = Color.rgb(0x6B, 0x6B, 0x6B)
    private val HAIR = Color.rgb(0xD9, 0xD9, 0xD9)
    private val WHITE = Color.WHITE

    private var wm: WindowManager? = null
    private var bar: View? = null
    private var lp: WindowManager.LayoutParams? = null
    private var mainBtn: TextView? = null
    private var statusView: TextView? = null
    private var progress: View? = null
    private var progressFill: View? = null
    private val main = Handler(Looper.getMainLooper())
    private val readerListener: () -> Unit = { refreshBar() }
    private val engineListener: () -> Unit = { refreshBar() }
    private var buttonCallback: Any? = null
    private val prefsListener = android.content.SharedPreferences.OnSharedPreferenceChangeListener { _, k ->
        if (k == "floatingBar") main.post { applyBarPref() }
    }

    private fun dp(v: Float) = v * resources.displayMetrics.density
    private fun dpi(v: Float) = dp(v).roundToInt()

    override fun onServiceConnected() {
        super.onServiceConnected()
        instance = this
        wm = getSystemService(WINDOW_SERVICE) as WindowManager
        Reader.addListener(readerListener)
        Engine.addListener(engineListener)
        getSharedPreferences("settings", MODE_PRIVATE).registerOnSharedPreferenceChangeListener(prefsListener)
        applyBarPref()
        if (Build.VERSION.SDK_INT >= 26) {
            val cb = object : AccessibilityButtonController.AccessibilityButtonCallback() {
                override fun onClicked(controller: AccessibilityButtonController) { mainAction() }
            }
            accessibilityButtonController.registerAccessibilityButtonCallback(cb)
            buttonCallback = cb
        }
        App.ensureFrontend(this)
    }

    /**
     * 审查采纳：记住最近一次“选中文字”事件（只在内存里，2 分钟内有效，不写文件）。
     * 有些 App（网页、阅读器）按下开始时已经查不到选区，但选中的那一刻系统会发这个事件。
     */
    override fun onAccessibilityEvent(event: AccessibilityEvent?) {
        val e = event ?: return
        if (e.eventType != AccessibilityEvent.TYPE_VIEW_TEXT_SELECTION_CHANGED || e.isPassword) return
        val pkg = e.packageName?.toString() ?: return
        if (pkg == packageName) return
        val a = minOf(e.fromIndex, e.toIndex); val b = maxOf(e.fromIndex, e.toIndex)
        if (a < 0 || b <= a) return
        // 审查采纳：拖动选区时这个事件很频繁——这里只记下文字引用和位置，不做跨进程查询、不复制字符串；按开始时才截取
        val cs: CharSequence? = e.text?.takeIf { it.size == 1 }?.get(0)
        TextGrab.lastSelection = TextGrab.Remembered(pkg, cs, a, b, if (cs == null) (try { e.source } catch (_: Throwable) { null }) else null, android.os.SystemClock.uptimeMillis())
    }
    override fun onInterrupt() {}

    override fun onDestroy() {
        instance = null
        TextGrab.lastSelection = null
        Reader.removeListener(readerListener)
        Engine.removeListener(engineListener)
        getSharedPreferences("settings", MODE_PRIVATE).unregisterOnSharedPreferenceChangeListener(prefsListener)
        if (Build.VERSION.SDK_INT >= 26) (buttonCallback as? AccessibilityButtonController.AccessibilityButtonCallback)?.let {
            try { accessibilityButtonController.unregisterAccessibilityButtonCallback(it) } catch (_: Throwable) {}
        }
        removeBar()
        super.onDestroy()
    }

    // ---------------- 快捷键 ----------------
    private var swallowUp = -1

    override fun onKeyEvent(event: KeyEvent): Boolean {
        val p = Prefs(this)
        if (event.action == KeyEvent.ACTION_UP && event.keyCode == swallowUp) { swallowUp = -1; return true }
        if (event.action != KeyEvent.ACTION_DOWN || Hotkey.isModifier(event.keyCode)) return false
        if (event.repeatCount > 0) return event.keyCode == swallowUp      // 按住不放：吞掉重复，不重复触发
        swallowUp = -1                                                     // 审查采纳：上一次的 UP 没收到也不会误吞后面的键
        val mainKey = Hotkey.parse(p.hotkey)
        val stopKey = Hotkey.parse(p.stopHotkey)
        return when {
            // 审查采纳：只认实体键盘（软键盘/虚拟按键注入的不算）
            event.deviceId == android.view.KeyCharacterMap.VIRTUAL_KEYBOARD -> false
            mainKey != null && mainKey.matches(event) -> { swallowUp = event.keyCode; main.post { mainAction() }; true }
            stopKey != null && stopKey.matches(event) -> { swallowUp = event.keyCode; main.post { Reader.stop() }; true }
            else -> false
        }
    }

    /** 主操作：空闲 → 取文字开始；朗读中 → 暂停；暂停 → 继续。 */
    fun mainAction() {
        if (Reader.toggle()) return
        val r = try { TextGrab.grab(this) } catch (t: Throwable) { Log.w(TAG, "grab failed", t); null }
        if (r == null) {
            hint("这里没有找到光标或选中的文字。\n也可以选中文字后在菜单里点「光标朗读」，或用“分享”发给光标朗读。")
            return
        }
        Log.i(TAG, "grab method=${r.method} chars=${r.text.length}")
        if (r.text.isBlank()) { hint(r.label); return }
        Reader.start(this, r.text, r.label)
    }

    private fun hint(msg: String) {
        Reader.notice(msg.lineSequence().first())
        Toast.makeText(this, msg, Toast.LENGTH_LONG).show()
    }

    // ---------------- 悬浮朗读条 ----------------
    private fun applyBarPref() { if (Prefs(this).floatingBar) showBar() else removeBar() }

    private fun text(s: String, sp: Float, color: Int, bold: Boolean = false) = TextView(this).apply {
        text = s
        setTextSize(TypedValue.COMPLEX_UNIT_SP, sp)
        setTextColor(color)
        typeface = Typeface.create(if (bold) "sans-serif-medium" else "sans-serif", Typeface.NORMAL)
        includeFontPadding = false
        gravity = Gravity.CENTER
    }

    private fun round(fill: Int, stroke: Int?, r: Float) = GradientDrawable().apply {
        setCornerRadius(dp(r)); setColor(fill); if (stroke != null) setStroke(max(1, dp(1f).roundToInt()), stroke)
    }

    @SuppressLint("ClickableViewAccessibility")
    private fun showBar() {
        if (bar != null) return
        val w = wm ?: return
        val p = Prefs(this)
        val outer = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            background = round(WHITE, INK, 14f)
            setPadding(0, 0, dpi(6f), 0)
        }
        // 左边黑条 = 拖动把手（和 Windows 版一样）
        val handle = View(this).apply {
            background = GradientDrawable().apply {
                setColor(INK); setCornerRadii(floatArrayOf(dp(14f), dp(14f), 0f, 0f, 0f, 0f, dp(14f), dp(14f)))
            }
            contentDescription = "拖动朗读条"
        }
        outer.addView(handle, LinearLayout.LayoutParams(dpi(10f), -1))
        val col = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(dpi(8f), dpi(6f), 0, dpi(5f)) }
        val row = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; setGravity(Gravity.CENTER_VERTICAL) }
        val mb = text("开始", 15f, INK, true).apply {
            background = round(WHITE, INK, 10f)
            setOnClickListener { mainAction() }
            setOnLongClickListener { Reader.stop(); true }
        }
        mainBtn = mb
        row.addView(mb, LinearLayout.LayoutParams(dpi(72f), dpi(40f)))
        val stop = text("停止", 15f, INK).apply {
            background = round(WHITE, INK, 10f)
            setOnClickListener { Reader.stop() }
        }
        row.addView(stop, LinearLayout.LayoutParams(dpi(56f), dpi(40f)).apply { leftMargin = dpi(6f) })
        val more = text("设置", 14f, GREY).apply {
            background = round(WHITE, HAIR, 10f)
            setOnClickListener {
                startActivity(Intent(this@CaretAccessibilityService, SettingsActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
            }
        }
        row.addView(more, LinearLayout.LayoutParams(dpi(52f), dpi(40f)).apply { leftMargin = dpi(6f) })
        col.addView(row)
        val status = text("", 11f, GREY).apply { gravity = Gravity.START; setSingleLine(true); setPadding(dpi(2f), dpi(5f), 0, 0) }
        statusView = status
        col.addView(status, LinearLayout.LayoutParams(dpi(196f), -2))
        val track = FrameLayout(this).apply { background = round(WHITE, INK, 2f); visibility = View.GONE }
        val fill = View(this).apply { setBackgroundColor(INK) }
        track.addView(fill, FrameLayout.LayoutParams(0, -1))
        progress = track; progressFill = fill
        col.addView(track, LinearLayout.LayoutParams(dpi(196f), dpi(5f)).apply { topMargin = dpi(4f) })
        outer.addView(col)

        val type = WindowManager.LayoutParams.TYPE_ACCESSIBILITY_OVERLAY
        // 不可获得焦点：点朗读条不会让正在编辑的输入框失去光标/选中（对应 Windows 版 WS_EX_NOACTIVATE）
        val params = WindowManager.LayoutParams(-2, -2, type,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL,
            PixelFormat.TRANSLUCENT)
        params.gravity = Gravity.TOP or Gravity.START
        val dm = resources.displayMetrics
        params.x = if (p.barX >= 0) p.barX.coerceIn(0, dm.widthPixels - dpi(60f)) else (dm.widthPixels - dpi(270f)).coerceAtLeast(0)
        params.y = if (p.barY >= 0) p.barY.coerceIn(0, dm.heightPixels - dpi(60f)) else (dm.heightPixels * 0.62f).toInt()
        lp = params

        var downX = 0f; var downY = 0f; var startX = 0; var startY = 0; var moved = false
        handle.setOnTouchListener { _, e ->
            when (e.actionMasked) {
                MotionEvent.ACTION_DOWN -> { downX = e.rawX; downY = e.rawY; startX = params.x; startY = params.y; moved = false; true }
                MotionEvent.ACTION_MOVE -> {
                    val dx = e.rawX - downX; val dy = e.rawY - downY
                    if (abs(dx) + abs(dy) > dp(4f)) moved = true
                    // 审查采纳：按朗读条实际大小限制，整条始终留在屏幕里
                    params.x = (startX + dx).toInt().coerceIn(0, maxOf(0, dm.widthPixels - outer.width))
                    params.y = (startY + dy).toInt().coerceIn(0, maxOf(0, dm.heightPixels - outer.height))
                    try { w.updateViewLayout(outer, params) } catch (_: Throwable) {}
                    true
                }
                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> { if (moved) { p.barX = params.x; p.barY = params.y }; true }
                else -> false
            }
        }
        try { w.addView(outer, params); bar = outer } catch (t: Throwable) { Log.e(TAG, "无法显示悬浮朗读条", t) }
        refreshBar()
    }

    private fun removeBar() {
        val b = bar ?: return
        try { wm?.removeView(b) } catch (_: Throwable) {}
        bar = null; mainBtn = null; statusView = null; progress = null; progressFill = null
    }

    private fun refreshBar() {
        val mb = mainBtn ?: return
        val st = Reader.state
        val (label, inverted) = when (st) {
            Reader.State.IDLE -> "开始" to false
            Reader.State.LOADING -> "加载中" to false
            Reader.State.READING -> "暂停" to true
            Reader.State.PAUSED -> "继续" to false
        }
        mb.text = label
        mb.setTextColor(if (inverted) WHITE else INK)
        mb.background = round(if (inverted) INK else WHITE, INK, 10f)
        mb.contentDescription = when (st) {
            Reader.State.IDLE -> "开始朗读：有选中读选中，没有就从光标处读"
            Reader.State.READING -> "暂停朗读"
            Reader.State.PAUSED -> "继续朗读"
            Reader.State.LOADING -> "正在加载音色"
        }
        val p = Prefs(this)
        statusView?.text = when (st) {
            Reader.State.IDLE -> (Reader.message.takeIf { it.isNotEmpty() && it != "读完了" && it != "已停止" }?.let { "$it · " } ?: "就绪 · ") + "${p.hotkey} 朗读"
            Reader.State.LOADING -> Engine.status
            Reader.State.READING -> Reader.label + " · 长按停止"
            Reader.State.PAUSED -> "已暂停 · ${p.hotkey} 继续 · ${p.stopHotkey} 停止"
        }
        val prog = Engine.loadProgress
        val pv = progress; val pf = progressFill
        if (pv != null && pf != null) {
            if (prog >= 0f) {
                pv.visibility = View.VISIBLE
                pf.layoutParams = (pf.layoutParams as FrameLayout.LayoutParams).apply { width = (dpi(196f) * prog).toInt() }
                pf.requestLayout()
            } else pv.visibility = View.GONE
        }
    }

    companion object {
        @Volatile var instance: CaretAccessibilityService? = null
            private set

        fun isEnabled(ctx: android.content.Context): Boolean {
            val s = android.provider.Settings.Secure.getString(ctx.contentResolver, android.provider.Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES) ?: return instance != null
            val me = ctx.packageName + "/" + CaretAccessibilityService::class.java.name
            return s.split(':').any { it.equals(me, true) || it.equals(ctx.packageName + "/.CaretAccessibilityService", true) } || instance != null
        }
    }
}
