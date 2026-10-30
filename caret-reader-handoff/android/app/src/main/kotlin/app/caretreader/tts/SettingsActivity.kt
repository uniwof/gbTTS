package app.caretreader.tts

import android.app.Activity
import android.app.AlertDialog
import android.content.ActivityNotFoundException
import android.content.Intent
import android.content.res.ColorStateList
import android.graphics.Bitmap
import android.graphics.BitmapShader
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.ColorFilter
import android.graphics.Paint
import android.graphics.Path
import android.graphics.PixelFormat
import android.graphics.Shader
import android.graphics.Typeface
import android.graphics.drawable.Drawable
import android.graphics.drawable.GradientDrawable
import android.graphics.drawable.RippleDrawable
import android.media.MediaPlayer
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.speech.tts.TextToSpeech
import android.text.InputType
import android.util.TypedValue
import android.view.Gravity
import android.view.KeyEvent
import android.view.View
import android.view.ViewGroup
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import java.io.File
import java.util.Locale
import kotlin.concurrent.thread
import kotlin.math.max
import kotlin.math.roundToInt

/**
 * 主界面（v0.5）：开启方式、朗读控制、音色（试听 / 导入 / 删除）、停顿、语速、线程、键盘快捷键、多音字词库、关于。
 * 风格：日式极简，只有黑白灰；细线；淡淡的网点纸面；苹果式圆角；没有渐变、阴影、人物。
 */
class SettingsActivity : Activity() {

    private val INK = Color.rgb(0x11, 0x11, 0x11)
    private val GREY = Color.rgb(0x6B, 0x6B, 0x6B)
    private val HAIR = Color.rgb(0xD9, 0xD9, 0xD9)
    private val PAPER = Color.rgb(0xFA, 0xFA, 0xFA)
    private val WHITE = Color.WHITE

    private lateinit var prefs: Prefs
    private lateinit var statusView: TextView
    private lateinit var readerView: TextView
    private lateinit var toggleBtn: TextView
    private lateinit var stopBtn: TextView
    private lateinit var setupView: TextView
    private lateinit var a11yBtn: TextView
    private lateinit var voiceCard: LinearLayout
    private lateinit var voiceNote: TextView
    private lateinit var frontendView: TextView
    private val voiceDots = LinkedHashMap<String, View>()
    private val voiceRows = LinkedHashMap<String, Pair<View, String>>()
    private val speakerViews = LinkedHashMap<String, SpeakerView>()
    private val refreshers = ArrayList<() -> Unit>()
    private var previewTts: TextToSpeech? = null
    private var previewReady = false
    private var player: MediaPlayer? = null
    private var playingId: String? = null
    private val engineListener: () -> Unit = { refreshStatus() }
    private val readerListener: () -> Unit = { refreshStatus() }

    private fun dp(v: Float) = v * resources.displayMetrics.density
    private fun dpi(v: Float) = dp(v).roundToInt()
    private val hairline get() = max(1, dp(0.75f).roundToInt())

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        prefs = Prefs(this)
        @Suppress("DEPRECATION")
        run {
            window.statusBarColor = PAPER
            if (Build.VERSION.SDK_INT >= 26) window.navigationBarColor = PAPER
            var flags = View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR
            if (Build.VERSION.SDK_INT >= 26) flags = flags or 0x10
            window.decorView.systemUiVisibility = flags
        }
        App.ensureFrontend(this)
        VoiceLibrary.refresh(this)
        setContentView(buildUi())
        Engine.preloadAsync(this)
    }

    override fun onResume() {
        super.onResume()
        Engine.addListener(engineListener)
        Reader.addListener(readerListener)
        refreshStatus()
    }

    override fun onPause() {
        super.onPause()
        Engine.removeListener(engineListener)
        Reader.removeListener(readerListener)
        previewTts?.stop()
        stopSample()
    }

    override fun onDestroy() {
        previewTts?.shutdown()
        previewTts = null
        super.onDestroy()
    }

    /** 试听：让系统用“本引擎”读一句示例（走真实的合成流程，包括多音字纠正）。 */
    private fun preview() {
        val text = if (Engine.currentVoice(this).engine == "melo")
            "这是光标朗读的语音示例。Hello，银行行长调到了总行。" else "这是光标朗读的语音示例。银行行长调到了总行。"
        Reader.pauseIfReading()
        val t = previewTts
        if (t != null && previewReady) { t.speak(text, TextToSpeech.QUEUE_FLUSH, null, "preview"); return }
        if (t != null) return
        previewTts = TextToSpeech(this, { status ->
            previewReady = status == TextToSpeech.SUCCESS
            if (previewReady) previewTts?.speak(text, TextToSpeech.QUEUE_FLUSH, null, "preview")
        }, packageName)
    }

    // =====================================================================
    private fun buildUi(): View {
        val cat = Engine.catalog(this)
        val scroll = ScrollView(this)
        scroll.background = ScreentoneDrawable()
        scroll.isFillViewport = true
        val col = LinearLayout(this)
        col.orientation = LinearLayout.VERTICAL
        val pad = dpi(20f)
        col.setPadding(pad, dpi(28f), pad, dpi(28f))
        scroll.addView(col, ViewGroup.LayoutParams(-1, -2))
        @Suppress("DEPRECATION")
        scroll.setOnApplyWindowInsetsListener { v, insets ->
            v.setPadding(insets.systemWindowInsetLeft, insets.systemWindowInsetTop, insets.systemWindowInsetRight, insets.systemWindowInsetBottom)
            insets
        }

        // ---- 标题 ----
        col.addView(text("光标朗读", 28f, INK, bold = true))
        col.addView(text("离线中文朗读 · ${cat.edition.ifEmpty { "标准版" }}", 13f, GREY).apply { setPadding(0, dpi(4f), 0, 0) })
        col.addView(rule(INK), LinearLayout.LayoutParams(-1, hairline).apply { topMargin = dpi(18f); bottomMargin = dpi(6f) })

        // ---- 朗读 ----
        col.addView(section("朗读"))
        val rc = card()
        readerView = text("", 14f, INK).apply { setPadding(dpi(16f), dpi(14f), dpi(16f), dpi(4f)) }
        rc.addView(readerView)
        val rrow = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; setPadding(dpi(12f), dpi(8f), dpi(12f), dpi(12f)) }
        toggleBtn = button("暂停", filled = true) { Reader.toggle() }
        stopBtn = button("停止", filled = false) { Reader.stop() }
        rrow.addView(toggleBtn, LinearLayout.LayoutParams(0, dpi(46f), 1f))
        rrow.addView(stopBtn, LinearLayout.LayoutParams(0, dpi(46f), 1f).apply { leftMargin = dpi(10f) })
        rc.addView(rrow)
        col.addView(rc)
        col.addView(note("开始朗读的方法：① 悬浮朗读条的「开始」（有选中读选中，没有就从光标处读到结尾）；② 选中文字 → 菜单里点「光标朗读」；③ 分享文字给「光标朗读」；④ 实体键盘快捷键；⑤ 系统无障碍按钮。"))

        // ---- 开启方式 ----
        col.addView(section("开启"))
        val sc = card()
        setupView = text("", 14f, INK).apply { setPadding(dpi(16f), dpi(14f), dpi(16f), dpi(6f)); setLineSpacing(dp(3f), 1f) }
        sc.addView(setupView)
        a11yBtn = button("开启悬浮朗读条（无障碍）", filled = true) { openA11ySettings() }
        sc.addView(a11yBtn, LinearLayout.LayoutParams(-1, dpi(48f)).apply { setMargins(dpi(12f), dpi(6f), dpi(12f), 0) })
        sc.addView(toggleRow("显示悬浮朗读条", { prefs.floatingBar }, { prefs.floatingBar = it }))
        if (Build.VERSION.SDK_INT >= 33) {
            sc.addView(button("允许通知（通知栏里显示暂停/停止）", filled = false) {
                requestPermissions(arrayOf("android.permission.POST_NOTIFICATIONS"), 9)
            }, LinearLayout.LayoutParams(-1, dpi(46f)).apply { setMargins(dpi(12f), dpi(4f), dpi(12f), 0) })
        }
        sc.addView(button("设为系统语音引擎（阅读 App 用）", filled = false) { openTtsSettings() },
            LinearLayout.LayoutParams(-1, dpi(46f)).apply { setMargins(dpi(12f), dpi(10f), dpi(12f), dpi(12f)) })
        col.addView(sc)
        col.addView(note("Android 13 及以上：如果无障碍里的开关是灰色的，先到 系统设置 → 应用 → 光标朗读 → 右上角 ⋮ →「允许受限制的设置」，再回来打开。"))

        // ---- 音色 ----
        col.addView(section("音色"))
        voiceCard = card()
        col.addView(voiceCard)
        statusView = text("", 12f, GREY).apply { setPadding(dpi(4f), dpi(8f), dpi(4f), 0) }
        col.addView(statusView)
        voiceNote = note("")
        col.addView(voiceNote)
        fillVoices()
        val irow = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        irow.addView(button("＋ 导入音色包", filled = false) { pickZip() }, LinearLayout.LayoutParams(0, dpi(46f), 1f))
        irow.addView(button("＋ 导入文件夹", filled = false) { pickTree() }, LinearLayout.LayoutParams(0, dpi(46f), 1f).apply { leftMargin = dpi(10f) })
        col.addView(irow, LinearLayout.LayoutParams(-1, -2).apply { topMargin = dpi(12f) })
        col.addView(button("试听当前音色（实时合成）", filled = false) { preview() }, LinearLayout.LayoutParams(-1, dpi(46f)).apply { topMargin = dpi(10f) })
        col.addView(note("点喇叭听预先录好的样音；长按导入的音色可以删除（内置音色不能删）。音色包就是电脑版的那种 .zip，一个包一个音色。"))

        // ---- 停顿 ----
        col.addView(section("标点停顿（秒）"))
        val pauseCard = card()
        val labels = listOf(
            Triple("comma", "逗号", "，、"), Triple("period", "句号", "。！？"), Triple("semicolon", "分号", "；"),
            Triple("colon", "冒号", "："), Triple("newline", "换行", "↵"),
        )
        labels.forEachIndexed { i, (key, name, marks) ->
            if (i > 0) pauseCard.addView(rule(HAIR), LinearLayout.LayoutParams(-1, hairline).apply { leftMargin = dpi(16f) })
            pauseCard.addView(stepperRow(name, marks, 0.05f, 0f, 3f, { prefs.pause(key) }, { prefs.setPause(key, it) }))
        }
        col.addView(pauseCard)

        // ---- 语速 ----
        col.addView(section("语速"))
        val speedCard = card()
        speedCard.addView(stepperRow("基础语速", "", 0.05f, 0.5f, 2.0f, { prefs.speed }, { prefs.speed = it }))
        col.addView(speedCard)
        col.addView(note("每个小句都按这个语速合成，节奏主要靠标点停顿。阅读 App 里设置的语速和音调会在此基础上生效。"))

        // ---- 性能 ----
        col.addView(section("性能"))
        val perfCard = card()
        perfCard.addView(stepperRow("线程数", "", 1f, 1f, Prefs.maxThreads().toFloat(), { prefs.threads.toFloat() }, {
            prefs.threads = it.roundToInt(); Engine.preloadAsync(this)
        }, decimals = 0))
        col.addView(perfCard)
        col.addView(note("默认 2。低端手机保持 2 即可；改动后会重新加载模型。"))

        // ---- 快捷键 ----
        col.addView(section("实体键盘快捷键"))
        val hk = card()
        hk.addView(hotkeyRow("开始 / 暂停 / 继续", { prefs.hotkey }, { prefs.hotkey = it }, { prefs.stopHotkey }))
        hk.addView(rule(HAIR), LinearLayout.LayoutParams(-1, hairline).apply { leftMargin = dpi(16f) })
        hk.addView(hotkeyRow("停止", { prefs.stopHotkey }, { prefs.stopHotkey = it }, { prefs.hotkey }))
        col.addView(hk)
        col.addView(note("接蓝牙/USB 键盘时，在任何 App 里按快捷键：开始（有选中读选中，没有就从光标处读）→ 暂停 → 继续；停止键彻底停止。需要开启上面的无障碍服务。"))

        // ---- 多音字 ----
        col.addView(section("多音字"))
        val fc = card()
        frontendView = text("", 14f, INK).apply { setPadding(dpi(16f), dpi(14f), dpi(16f), dpi(6f)) }
        fc.addView(frontendView)
        fc.addView(button("编辑用户自定义词库", filled = false) { editUserDict() },
            LinearLayout.LayoutParams(-1, dpi(46f)).apply { setMargins(dpi(12f), dpi(6f), dpi(12f), dpi(12f)) })
        col.addView(fc)
        col.addView(note("一行一个词：“词 拼音”，例如“银行 yin2 hang2”。优先级最高，覆盖内置词库。保存后立即生效。"))

        // ---- 底部 ----
        col.addView(button("关于 / 第三方许可", filled = false) { showAbout() }, LinearLayout.LayoutParams(-1, dpi(46f)).apply { topMargin = dpi(28f) })
        val reset = button("恢复默认", filled = false) {
            prefs.resetNumbers(); refreshers.forEach { it() }; Engine.preloadAsync(this)
        }
        col.addView(reset, LinearLayout.LayoutParams(-1, dpi(46f)).apply { topMargin = dpi(12f) })
        col.addView(text("完全离线 · 不联网 · v" + versionName(), 11f, GREY).apply {
            gravity = Gravity.CENTER_HORIZONTAL; setPadding(0, dpi(26f), 0, 0)
        }, LinearLayout.LayoutParams(-1, -2))
        return scroll
    }

    private fun versionName(): String = try {
        @Suppress("DEPRECATION")
        packageManager.getPackageInfo(packageName, 0).versionName ?: ""
    } catch (_: Throwable) { "" }

    private fun refreshStatus() {
        if (!::statusView.isInitialized) return
        val prog = Engine.loadProgress
        statusView.text = "当前：" + Engine.currentVoice(this).name + "　·　" + Engine.status + if (prog >= 0) "  ${(prog * 100).roundToInt()}%" else ""
        val st = Reader.state
        readerView.text = Reader.stateText() + (if (Reader.message.isNotEmpty()) "　·　" + Reader.message else "")
        toggleBtn.text = if (st == Reader.State.PAUSED) "继续" else "暂停"
        toggleBtn.alpha = if (st == Reader.State.IDLE) 0.35f else 1f
        stopBtn.alpha = if (st == Reader.State.IDLE) 0.35f else 1f
        val on = CaretAccessibilityService.isEnabled(this)
        setupView.text = (if (on) "■ 无障碍服务：已开启" else "□ 无障碍服务：未开启") +
            "\n" + (if (on) "悬浮朗读条、键盘快捷键、无障碍按钮可以用了。" else "开启后才有悬浮朗读条、键盘快捷键和“从光标处朗读”。")
        a11yBtn.text = if (on) "无障碍设置" else "开启悬浮朗读条（无障碍）"
        frontendView.text = when {
            PolyphoneFrontend.isReady() -> "■ 多音字纠正：已启用"
            PolyphoneFrontend.lastError != null -> "□ 多音字纠正：没能加载（${PolyphoneFrontend.lastError}），按旧方式朗读"
            else -> "… 多音字纠正：正在加载词库"
        }
        if (!PolyphoneFrontend.isReady() && PolyphoneFrontend.lastError == null) readerView.postDelayed({ refreshStatus() }, 800)
    }

    // ---------------- 音色列表 ----------------
    private fun fillVoices() {
        val cat = VoiceLibrary.catalog(this)
        voiceCard.removeAllViews(); voiceDots.clear(); voiceRows.clear(); speakerViews.clear()
        cat.voices.forEachIndexed { i, v ->
            if (i > 0) voiceCard.addView(rule(HAIR), LinearLayout.LayoutParams(-1, hairline).apply { leftMargin = dpi(16f) })
            voiceCard.addView(voiceRow(v))
        }
        voiceNote.text = if (cat.problems.isEmpty()) "同一模型里的音色切换是瞬间的；换到另一个模型要重新加载几秒。同一时间只占用一个模型的内存。"
            else "有 ${cat.problems.size} 个音色包没法用：" + cat.problems.joinToString("；")
        paintDots()
        refreshStatus()
    }

    private fun voiceRow(v: VoiceSpec): View {
        val row = LinearLayout(this)
        row.orientation = LinearLayout.HORIZONTAL
        row.setGravity(Gravity.CENTER_VERTICAL)
        row.minimumHeight = dpi(58f)
        row.setPadding(dpi(16f), dpi(8f), dpi(10f), dpi(8f))
        val m = Regex("^(.*?)\\s*\\((.*)\\)\\s*$").find(v.name)
        val mainName = m?.groupValues?.get(1) ?: v.name
        val sub = (m?.groupValues?.get(2) ?: "") + if (v.builtIn) " · 内置" else ""
        val texts = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        texts.addView(text(mainName, 16f, INK))
        texts.addView(text(sub.trim(' ', '·'), 12f, GREY).apply { setPadding(0, dpi(3f), 0, 0) })
        row.addView(texts, LinearLayout.LayoutParams(0, -2, 1f))
        if (v.sample != null) {
            val sp = SpeakerView()
            sp.contentDescription = "试听 $mainName"
            sp.setOnClickListener { toggleSample(v) }
            speakerViews[v.id] = sp
            row.addView(sp, LinearLayout.LayoutParams(dpi(44f), dpi(44f)).apply { rightMargin = dpi(6f) })
        }
        val dot = View(this)
        voiceDots[v.id] = dot
        row.addView(dot, LinearLayout.LayoutParams(dpi(18f), dpi(18f)).apply { rightMargin = dpi(6f) })
        pressable(row, 12f, null)
        voiceRows[v.id] = Pair(row, mainName + (if (sub.isNotBlank()) "，$sub" else ""))
        row.setOnClickListener {
            if (Reader.isActive()) Reader.stop()
            prefs.voiceId = v.id
            paintDots()
            refreshStatus()
            Engine.preloadAsync(this)
        }
        if (!v.builtIn) row.setOnLongClickListener {
            AlertDialog.Builder(this).setTitle("删除音色").setMessage("删除「${v.name}」？\n删除后可以重新导入。")
                .setPositiveButton("删除") { _, _ ->
                    if (Reader.isActive()) Reader.stop()
                    Engine.releaseIfIdle("删除音色")
                    VoiceLibrary.delete(this, v)
                    if (prefs.voiceId == v.id) prefs.voiceId = null
                    fillVoices()
                }.setNegativeButton("取消", null).show()
            true
        }
        return row
    }

    private fun paintDots() {
        val cur = Engine.currentVoice(this).id
        for ((id, rv) in voiceRows) rv.first.contentDescription = rv.second + if (id == cur) "，已选中" else "，未选中"
        for ((id, dot) in voiceDots) {
            dot.background = GradientDrawable().apply {
                setShape(GradientDrawable.OVAL)
                if (id == cur) setColor(INK) else { setColor(WHITE); setStroke(max(1, dp(1f).roundToInt()), INK) }
            }
        }
    }

    // ---------------- 样音试听（预先做好的 sample.mp3） ----------------
    private fun toggleSample(v: VoiceSpec) {
        if (playingId == v.id) { stopSample(); return }
        stopSample()
        Reader.pauseIfReading()          // 和 Windows 版一样：试听会暂停正在进行的朗读
        try {
            val mp = MediaPlayer()
            if (v.sampleInAssets) assets.openFd(v.sample!!).use { mp.setDataSource(it.fileDescriptor, it.startOffset, it.length) }
            else mp.setDataSource(v.sample!!)
            mp.setOnCompletionListener { stopSample() }
            mp.prepare(); mp.start()
            player = mp; playingId = v.id
            speakerViews[v.id]?.playing = true
        } catch (t: Throwable) {
            Toast.makeText(this, "样音播放失败：${t.message}", Toast.LENGTH_SHORT).show()
            stopSample()
        }
    }

    private fun stopSample() {
        try { player?.stop() } catch (_: Throwable) {}
        try { player?.release() } catch (_: Throwable) {}
        player = null
        playingId?.let { speakerViews[it]?.playing = false }
        playingId = null
    }

    /** 小喇叭图标（自己画，黑白，播放中反色）。 */
    private inner class SpeakerView : View(this@SettingsActivity) {
        var playing = false
            set(v) { field = v; invalidate() }
        private val p = Paint(Paint.ANTI_ALIAS_FLAG)
        init { isClickable = true; isFocusable = true }
        override fun onDraw(c: Canvas) {
            val w = width.toFloat(); val h = height.toFloat(); val r = minOf(w, h) / 2 - dp(3f)
            p.style = Paint.Style.FILL; p.color = if (playing) INK else WHITE
            c.drawCircle(w / 2, h / 2, r, p)
            p.style = Paint.Style.STROKE; p.strokeWidth = dp(1f); p.color = INK
            c.drawCircle(w / 2, h / 2, r, p)
            val fg = if (playing) WHITE else INK
            val s = r * 0.42f; val cx = w / 2 - s * 0.5f; val cy = h / 2
            val path = Path().apply {
                moveTo(cx - s * 0.8f, cy - s * 0.35f); lineTo(cx - s * 0.3f, cy - s * 0.35f); lineTo(cx + s * 0.35f, cy - s * 0.9f)
                lineTo(cx + s * 0.35f, cy + s * 0.9f); lineTo(cx - s * 0.3f, cy + s * 0.35f); lineTo(cx - s * 0.8f, cy + s * 0.35f); close()
            }
            p.style = Paint.Style.FILL; p.color = fg; c.drawPath(path, p)
            p.style = Paint.Style.STROKE; p.strokeWidth = dp(1.4f); p.strokeCap = Paint.Cap.ROUND
            c.drawArc(cx + s * 0.1f, cy - s * 0.6f, cx + s * 1.3f, cy + s * 0.6f, -50f, 100f, false, p)
            c.drawArc(cx - s * 0.2f, cy - s * 1.05f, cx + s * 1.8f, cy + s * 1.05f, -50f, 100f, false, p)
        }
    }

    // ---------------- 导入 ----------------
    private fun pickZip() {
        val i = Intent(Intent.ACTION_OPEN_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("*/*")
            .putExtra(Intent.EXTRA_MIME_TYPES, arrayOf("application/zip", "application/x-zip-compressed", "application/octet-stream"))
        try { startActivityForResult(i, REQ_ZIP) } catch (_: ActivityNotFoundException) { Toast.makeText(this, "找不到系统文件选择器", Toast.LENGTH_LONG).show() }
    }

    private fun pickTree() {
        try { startActivityForResult(Intent(Intent.ACTION_OPEN_DOCUMENT_TREE), REQ_TREE) }
        catch (_: ActivityNotFoundException) { Toast.makeText(this, "找不到系统文件选择器", Toast.LENGTH_LONG).show() }
    }

    @Deprecated("Deprecated in Java")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        @Suppress("DEPRECATION") super.onActivityResult(requestCode, resultCode, data)
        val uri: Uri = data?.data ?: return
        if (resultCode != RESULT_OK || (requestCode != REQ_ZIP && requestCode != REQ_TREE)) return
        val msg = text("正在准备…", 15f, INK).apply { setPadding(dpi(24f), dpi(20f), dpi(24f), dpi(20f)) }
        val dlg = AlertDialog.Builder(this).setTitle("导入音色").setView(msg).setCancelable(false).show()
        thread(name = "voice-import") {
            val r = if (requestCode == REQ_ZIP) VoiceLibrary.importZip(this, uri) { s -> runOnUiThread { msg.text = s } }
                    else VoiceLibrary.importTree(this, uri) { s -> runOnUiThread { msg.text = s } }
            runOnUiThread {
                try { dlg.dismiss() } catch (_: Throwable) {}
                val sb = StringBuilder()
                if (r.added.isNotEmpty()) sb.append("已导入：").append(r.added.joinToString("、")).append("\n")
                if (r.skipped.isNotEmpty()) sb.append("跳过：").append(r.skipped.joinToString("、")).append("\n")
                if (r.errors.isNotEmpty()) sb.append("没能导入：").append(r.errors.joinToString("；"))
                AlertDialog.Builder(this).setTitle("导入结果").setMessage(sb.toString().trim().ifEmpty { "没有变化" }).setPositiveButton("好", null).show()
                if (r.added.isNotEmpty() && Engine.catalog(this).voice(prefs.voiceId) == null) prefs.voiceId = null
                fillVoices()
            }
        }
    }

    // ---------------- 快捷键 ----------------
    private fun hotkeyRow(name: String, get: () -> String, set: (String) -> Unit, other: () -> String): View {
        val row = LinearLayout(this)
        row.orientation = LinearLayout.HORIZONTAL
        row.setGravity(Gravity.CENTER_VERTICAL)
        row.minimumHeight = dpi(56f)
        row.setPadding(dpi(16f), dpi(8f), dpi(12f), dpi(8f))
        row.addView(text(name, 16f, INK), LinearLayout.LayoutParams(0, -2, 1f))
        val value = text("", 15f, INK).apply {
            gravity = Gravity.CENTER; typeface = Typeface.MONOSPACE
            background = roundRect(WHITE, HAIR, 10f); setPadding(dpi(10f), dpi(8f), dpi(10f), dpi(8f))
        }
        fun show() { value.text = get(); value.contentDescription = "$name 快捷键 ${get()}，点按修改" }
        show(); refreshers.add { show() }
        row.addView(value)
        pressable(row, 12f, null)
        row.setOnClickListener {
            val tip = text("请在键盘上按下新的组合键\n（例如 Ctrl+Alt+R）", 15f, INK).apply { setPadding(dpi(24f), dpi(20f), dpi(24f), dpi(20f)); gravity = Gravity.CENTER }
            val d = AlertDialog.Builder(this).setTitle("设置「$name」快捷键").setView(tip).setNegativeButton("取消", null).create()
            d.setOnKeyListener { _, code, e ->
                if (e.action != KeyEvent.ACTION_DOWN || Hotkey.isModifier(code)) return@setOnKeyListener false
                if (code == KeyEvent.KEYCODE_BACK && e.modifiers == 0) return@setOnKeyListener false
                val h = Hotkey.from(e)
                val prob = Hotkey.problem(h, Hotkey.parse(other()))
                if (prob != null) tip.text = "$h 不能用：$prob\n\n请换一个"
                else { set(h.toString()); show(); d.dismiss(); Toast.makeText(this, "已改为 $h", Toast.LENGTH_SHORT).show() }
                true
            }
            d.show()
        }
        return row
    }

    // ---------------- 用户词库 / 关于 ----------------
    private fun editUserDict() {
        val f = App.userDictFile(this)
        val et = EditText(this).apply {
            setText(try { f.readText(Charsets.UTF_8) } catch (_: Throwable) { "" })
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 14f)
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_MULTI_LINE or InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS
            gravity = Gravity.TOP or Gravity.START
            minLines = 10
            setHorizontallyScrolling(false)
        }
        val box = ScrollView(this).apply { addView(et); setPadding(dpi(16f), dpi(8f), dpi(16f), 0) }
        AlertDialog.Builder(this).setTitle("用户自定义词库").setView(box)
            .setPositiveButton("保存") { _, _ ->
                try {
                    val tmp = File(f.parentFile, f.name + ".tmp")
                    tmp.writeText(et.text.toString(), Charsets.UTF_8)
                    if (!tmp.renameTo(f)) { f.writeText(et.text.toString(), Charsets.UTF_8); tmp.delete() }
                    App.reloadFrontend(this)
                    Toast.makeText(this, "已保存，正在重新加载词库", Toast.LENGTH_SHORT).show()
                    refreshStatus()
                } catch (t: Throwable) { Toast.makeText(this, "保存失败：${t.message}", Toast.LENGTH_LONG).show() }
            }.setNegativeButton("取消", null).show()
    }

    private fun showAbout() {
        val notices = try { assets.open("THIRD_PARTY_NOTICES.txt").bufferedReader().use { it.readText() } } catch (_: Throwable) { "（缺少许可说明文件）" }
        val tv = text("光标朗读 v${versionName()} · ${Engine.catalog(this).edition}\n完全离线，不联网。语音引擎 sherpa-onnx（Apache-2.0）。\n\n" + notices, 12f, INK).apply {
            gravity = Gravity.START; setPadding(dpi(20f), dpi(12f), dpi(20f), dpi(12f)); setTextIsSelectable(true)
        }
        AlertDialog.Builder(this).setTitle("关于 / 第三方许可").setView(ScrollView(this).apply { addView(tv) }).setPositiveButton("好", null).show()
    }

    private fun toggleRow(name: String, get: () -> Boolean, set: (Boolean) -> Unit): View {
        val row = LinearLayout(this)
        row.orientation = LinearLayout.HORIZONTAL
        row.setGravity(Gravity.CENTER_VERTICAL)
        row.minimumHeight = dpi(52f)
        row.setPadding(dpi(16f), dpi(6f), dpi(16f), dpi(6f))
        row.addView(text(name, 15f, INK), LinearLayout.LayoutParams(0, -2, 1f))
        val box = View(this)
        fun paint() {
            box.background = roundRect(if (get()) INK else WHITE, INK, 6f)
            row.contentDescription = name + if (get()) "，已打开" else "，已关闭"
        }
        row.addView(box, LinearLayout.LayoutParams(dpi(22f), dpi(22f)))
        paint()
        pressable(row, 12f, null)
        row.setOnClickListener { set(!get()); paint() }
        return row
    }

    private fun openA11ySettings() {
        try { startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) }
        catch (_: Throwable) { openTtsSettings() }
    }

    companion object {
        private const val REQ_ZIP = 41
        private const val REQ_TREE = 42
    }

    // ---------------- 小部件 ----------------
    private fun text(s: String, sp: Float, color: Int, bold: Boolean = false) = TextView(this).apply {
        text = s
        setTextSize(TypedValue.COMPLEX_UNIT_SP, sp)
        setTextColor(color)
        typeface = if (bold) Typeface.create("sans-serif-medium", Typeface.NORMAL) else Typeface.create("sans-serif", Typeface.NORMAL)
        includeFontPadding = false
        if (bold) letterSpacing = 0.04f
    }

    private fun section(s: String) = text(s, 12f, GREY).apply {
        letterSpacing = 0.12f
        setPadding(dpi(4f), dpi(24f), 0, dpi(8f))
    }

    private fun note(s: String) = text(s, 12f, GREY).apply {
        setLineSpacing(dp(3f), 1f)
        setPadding(dpi(4f), dpi(8f), dpi(4f), 0)
    }

    private fun rule(color: Int) = View(this).apply { setBackgroundColor(color) }

    private fun roundRect(fill: Int, stroke: Int?, radiusDp: Float) = GradientDrawable().apply {
        setShape(GradientDrawable.RECTANGLE)
        setCornerRadius(dp(radiusDp))
        setColor(fill)
        if (stroke != null) setStroke(hairline, stroke)
    }

    private fun card() = LinearLayout(this).apply {
        orientation = LinearLayout.VERTICAL
        background = roundRect(WHITE, INK, 14f)
        val p = hairline
        setPadding(p, p, p, p)
    }

    private fun pressable(view: View, radiusDp: Float, base: Drawable?) {
        val mask = roundRect(Color.BLACK, null, radiusDp)
        view.background = RippleDrawable(ColorStateList.valueOf(Color.argb(0x22, 0, 0, 0)), base, mask)
        view.isClickable = true
        view.isFocusable = true
    }

    private fun stepperRow(
        name: String, marks: String, step: Float, min: Float, maxV: Float,
        get: () -> Float, set: (Float) -> Unit, decimals: Int = 2,
    ): View {
        val row = LinearLayout(this)
        row.orientation = LinearLayout.HORIZONTAL
        row.setGravity(Gravity.CENTER_VERTICAL)
        row.minimumHeight = dpi(56f)
        row.setPadding(dpi(16f), dpi(8f), dpi(12f), dpi(8f))
        row.addView(text(name, 16f, INK))
        row.addView(text(marks, 14f, GREY).apply { setPadding(dpi(10f), 0, 0, 0) }, LinearLayout.LayoutParams(0, -2, 1f))
        val value = text("", 16f, INK).apply {
            gravity = Gravity.CENTER
            typeface = Typeface.MONOSPACE
            background = roundRect(WHITE, HAIR, 10f)
            setPadding(dpi(8f), dpi(6f), dpi(8f), dpi(6f))
        }
        fun show() {
            value.text = String.format(Locale.US, "%.${decimals}f", get())
            value.contentDescription = name + " " + value.text
        }
        fun change(delta: Float) {
            val n = ((get() + delta) / step).roundToInt() * step
            set(n.coerceIn(min, maxV))
            show()
        }
        val minus = stepButton("−", "减少$name") { change(-step) }
        val plus = stepButton("+", "增加$name") { change(step) }
        // 按钮做到 48×44dp，手指好按（安卓无障碍建议触控区域≥48dp）
        row.addView(minus, LinearLayout.LayoutParams(dpi(48f), dpi(44f)))
        row.addView(value, LinearLayout.LayoutParams(dpi(60f), dpi(44f)).apply { leftMargin = dpi(6f); rightMargin = dpi(6f) })
        row.addView(plus, LinearLayout.LayoutParams(dpi(48f), dpi(44f)))
        show()
        refreshers.add { show() }
        return row
    }

    private fun stepButton(label: String, desc: String, onClick: () -> Unit) = text(label, 18f, INK).apply {
        gravity = Gravity.CENTER
        contentDescription = desc
        pressable(this, 10f, roundRect(WHITE, INK, 10f))
        setOnClickListener { onClick() }
    }

    private fun button(label: String, filled: Boolean, onClick: () -> Unit) = text(label, 16f, if (filled) WHITE else INK).apply {
        gravity = Gravity.CENTER
        letterSpacing = 0.06f
        pressable(this, 12f, if (filled) roundRect(INK, null, 12f) else roundRect(WHITE, INK, 12f))
        setOnClickListener { onClick() }
    }

    private fun openTtsSettings() {
        // TTS_SETTINGS 不是公开常量，但 AOSP 和绝大多数品牌都有；没有就退到“无障碍”（很多国产系统把“文字转语音输出”放在这里），最后才是设置首页
        val intents = listOf(Intent("com.android.settings.TTS_SETTINGS"), Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS), Intent(Settings.ACTION_SETTINGS))
        for (i in intents) {
            try {
                startActivity(i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)); return
            } catch (_: ActivityNotFoundException) {
            } catch (_: SecurityException) {
            }
        }
    }

    /** 纸白底 + 错位网点（漫画网点纸的质感），用平铺小图实现，几乎不耗性能。 */
    private inner class ScreentoneDrawable : Drawable() {
        private val paint = Paint(Paint.ANTI_ALIAS_FLAG)

        init {
            val g = max(4, dpi(7f))
            val bmp = Bitmap.createBitmap(g * 2, g * 2, Bitmap.Config.ARGB_8888)
            val c = Canvas(bmp)
            c.drawColor(PAPER)
            val p = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.rgb(0xE3, 0xE3, 0xE3) }
            val r = max(0.8f, dp(0.6f))
            c.drawCircle(g / 2f, g / 2f, r, p)
            c.drawCircle(g * 1.5f, g * 1.5f, r, p)
            paint.shader = BitmapShader(bmp, Shader.TileMode.REPEAT, Shader.TileMode.REPEAT)
        }

        override fun draw(canvas: Canvas) { canvas.drawRect(bounds, paint) }
        override fun setAlpha(alpha: Int) {}
        override fun setColorFilter(colorFilter: ColorFilter?) {}
        @Deprecated("Deprecated in Java")
        override fun getOpacity(): Int = PixelFormat.OPAQUE
    }
}
