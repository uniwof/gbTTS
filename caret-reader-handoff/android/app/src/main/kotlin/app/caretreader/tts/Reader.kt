package app.caretreader.tts

import android.content.Context
import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioTrack
import android.os.Handler
import android.os.Looper
import android.util.Log

/**
 * 光标朗读播放器（对应 Windows 版 MainForm 的朗读状态机 + SpeechPipeline + NAudioSink）。
 *
 * 状态：IDLE → LOADING（加载模型）→ READING ⇄ PAUSED → IDLE。
 * 主操作 toggle()：空闲时由调用方给出文字开始；朗读中 = 暂停；暂停中 = 从暂停处继续。
 * stop()：彻底停止。
 * 暂停在声音输出口里完成（AudioTrack.pause/play），缓冲的声音不丢，从停下的那个字接着读；
 * 暂停时合成线程写不进去就自然停下（最多提前合成约 1 秒 + 当前一句）。
 */
object Reader {
    enum class State { IDLE, LOADING, READING, PAUSED }

    @Volatile var state = State.IDLE
        private set
    /** 给界面/通知看的一句话（例如“朗读选中 · 12 句”“这里没有可以朗读的文字”）。 */
    @Volatile var message: String = ""
        private set
    /** 这次读的是什么（选中的文字 / 光标后的文字 / 分享的文字）。 */
    @Volatile var label: String = ""
        private set

    private val lock = Object()
    private var gen = 0L
    @Volatile private var stopGen = -1L
    @Volatile private var pausedFlag = false
    private var track: AudioTrack? = null
    private var worker: Thread? = null
    private val main = Handler(Looper.getMainLooper())
    private val listeners = ArrayList<() -> Unit>()
    private var focusListener: AudioManager.OnAudioFocusChangeListener? = null

    fun isActive() = state != State.IDLE

    fun addListener(l: () -> Unit) = synchronized(listeners) { listeners.add(l) }
    fun removeListener(l: () -> Unit) = synchronized(listeners) { listeners.remove(l) }
    private fun changed() {
        val ls = synchronized(listeners) { listeners.toList() }
        main.post { ls.forEach { it() } }
    }

    private fun set(s: State, msg: String? = null) {
        state = s
        if (msg != null) message = msg
        changed()
    }

    /** 只显示一条提示（不改变状态）。 */
    fun notice(msg: String) { message = msg; changed() }

    /**
     * 开始朗读一段文字（正在朗读的会先停掉）。
     * @param label 例如“朗读选中”“从光标处朗读”“朗读分享的文字”
     */
    fun start(ctx: Context, text: String, label: String) {
        val app = ctx.applicationContext
        val myGen: Long
        synchronized(lock) {
            // 审查采纳：“停掉旧的 + 开始新的”在同一把锁里完成，两个入口同时触发也不会两路声音重叠
            stopGen = gen
            try { track?.pause() } catch (_: Throwable) {}
            myGen = ++gen
            pausedFlag = false
            (lock as Object).notifyAll()
        }
        this.label = label
        val prefs = Prefs(app)
        val voice = Engine.currentVoice(app)
        set(if (Engine.isLoaded(voice, prefs.threads)) State.READING else State.LOADING, "$label · ${voice.name}")
        ReaderService.ensureRunning(app)
        // 审查采纳：清理和切分放到后台线程（10 万字也只要几十毫秒，但不占用界面线程）
        val th = Thread({
            val cleaned = TextGrab.clean(text)
            val clauses = TextSplitter.split(cleaned.take(100_000), prefs.pauses())
            if (cleaned.length > 100_000) { this.label = "$label（太长，只读前 10 万字）"; message = "${this.label} · ${voice.name}"; changed() }
            if (clauses.isEmpty()) {
                if (gen == myGen && stopGen < myGen) { set(State.IDLE, "没有可以朗读的文字"); ReaderService.stopIfIdle(app) }
                synchronized(lock) { if (gen == myGen) worker = null }
                return@Thread
            }
            run(app, myGen, clauses, voice, prefs)
        }, "reader")
        th.isDaemon = true
        synchronized(lock) { worker = th }
        th.start()
    }

    /** 主操作（快捷键 / 悬浮条主按钮）：朗读中 → 暂停；暂停中 → 继续；加载中 → 记住“开始后先暂停”。返回 false = 空闲（调用方应去取文字开始朗读）。 */
    fun toggle(): Boolean {
        synchronized(lock) {
            when (state) {
                State.IDLE -> return false
                State.LOADING -> { pausedFlag = !pausedFlag; message = if (pausedFlag) "加载完成后先暂停" else message; changed(); return true }
                State.READING -> { pausedFlag = true; try { track?.pause() } catch (_: Throwable) {}; set(State.PAUSED); return true }
                State.PAUSED -> { pausedFlag = false; try { track?.play() } catch (_: Throwable) {}; (lock as Object).notifyAll(); set(State.READING); return true }
            }
        }
    }

    /** 外部要求暂停（来电、别的 App 抢了声音、试听音色）。 */
    fun pauseIfReading() { if (state == State.READING) toggle() }

    fun stop() {
        synchronized(lock) {
            stopGen = gen
            pausedFlag = false
            // 只暂停（会打断正在阻塞的 write）；flush/stop/release 由合成线程自己在 finally 里做（审查采纳：不跨线程操作 AudioTrack）
            try { track?.pause() } catch (_: Throwable) {}
            (lock as Object).notifyAll()
        }
        // 不等合成线程：声音已经立刻停了；正在合成的那一句合成完会被丢弃，线程自己退出（新的朗读会排队等引擎锁）
        if (state != State.IDLE) set(State.IDLE, "已停止")
    }

    private fun run(app: Context, myGen: Long, clauses: List<Clause>, voice: VoiceSpec, prefs: Prefs) {
        val stopped = { stopGen >= myGen }
        var at: AudioTrack? = null
        var written = 0L
        try {
            requestFocus(app)
            Engine.use(app, voice, prefs.threads) { synth ->
                if (stopped()) return@use
                val sr = synth.sampleRate
                val minBuf = AudioTrack.getMinBufferSize(sr, AudioFormat.CHANNEL_OUT_MONO, AudioFormat.ENCODING_PCM_16BIT).let { if (it <= 0) sr * 2 else it }
                val t = AudioTrack.Builder()
                    .setAudioAttributes(AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_MEDIA)
                        .setContentType(AudioAttributes.CONTENT_TYPE_SPEECH).build())
                    .setAudioFormat(AudioFormat.Builder().setSampleRate(sr).setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                        .setChannelMask(AudioFormat.CHANNEL_OUT_MONO).build())
                    .setBufferSizeInBytes(maxOf(minBuf, sr * 4))     // 约 2 秒（审查采纳：低端机更不容易断音）
                    .setTransferMode(AudioTrack.MODE_STREAM)
                    .build()
                at = t
                synchronized(lock) {
                    if (stopped()) return@use
                    track = t
                    if (!pausedFlag) t.play()
                    set(if (pausedFlag) State.PAUSED else State.READING, "$label · ${voice.name}")
                }
                val out = PcmOut { pcm, off, len ->
                    var o = off
                    val end = off + len
                    while (o < end) {
                        if (stopped()) return@PcmOut false
                        if (pausedFlag) { synchronized(lock) { if (pausedFlag && !stopped()) (lock as Object).wait(200) }; continue }
                        val n = t.write(pcm, o, end - o)
                        if (n < 0) { if (!stopped()) message = "朗读失败：声音输出出错（$n）"; return@PcmOut false }
                        o += n
                        written += n / 2
                    }
                    true
                }
                val speed = (prefs.speed * voice.speed).coerceIn(0.3f, 4f)
                val st = SpeechRenderer(synth, voice.sid, speed, 1f, 1.0, out, stopped, gain = voice.gain).render(clauses)
                Log.i(TAG, "reader clauses=${st.clauses} first=${st.firstAudioMs}ms rtf=${"%.2f".format(st.rtf)} stopped=${st.stopped}")
                // 等最后的声音播完（暂停时不算时间）
                if (!st.stopped) {
                    var last = -1; var still = 0
                    while (!stopped()) {
                        val head = t.playbackHeadPosition
                        if ((((head.toLong() and 0xffffffffL) - (written and 0xffffffffL)) and 0xffffffffL) < 0x80000000L) break   // 32 位回绕安全的比较
                        if (!pausedFlag) { if (head == last) still++ else still = 0; if (still > 40) break }   // 2 秒不动 = 设备异常，结束
                        last = head
                        Thread.sleep(50)
                    }
                }
            }
        } catch (t: Throwable) {
            Log.e(TAG, "reader failed", t)
            if (!stopped()) message = "朗读失败：" + (if (t is OutOfMemoryError) "内存不足" else (t.message ?: t.javaClass.simpleName))
        } finally {
            synchronized(lock) {
                if (track === at) track = null
                if (gen == myGen) { worker = null }
            }
            try { at?.pause(); at?.flush() } catch (_: Throwable) {}
            try { at?.stop() } catch (_: Throwable) {}
            try { at?.release() } catch (_: Throwable) {}
            if (gen == myGen) {
                abandonFocus(app)
                val m = if (stopGen >= myGen) "已停止" else if (message.startsWith("朗读失败")) message else "读完了"
                set(State.IDLE, m)
                ReaderService.stopIfIdle(app)
            }
        }
    }

    // ---------------- 音频焦点：来电、别的 App 开始放声音时自动暂停 ----------------
    @Suppress("DEPRECATION")
    private fun requestFocus(ctx: Context) {
        val am = ctx.getSystemService(Context.AUDIO_SERVICE) as AudioManager
        val l = AudioManager.OnAudioFocusChangeListener { f ->
            if (f == AudioManager.AUDIOFOCUS_LOSS || f == AudioManager.AUDIOFOCUS_LOSS_TRANSIENT) pauseIfReading()
        }
        focusListener = l
        try { am.requestAudioFocus(l, AudioManager.STREAM_MUSIC, AudioManager.AUDIOFOCUS_GAIN_TRANSIENT) } catch (_: Throwable) {}
    }

    @Suppress("DEPRECATION")
    private fun abandonFocus(ctx: Context) {
        val l = focusListener ?: return
        try { (ctx.getSystemService(Context.AUDIO_SERVICE) as AudioManager).abandonAudioFocus(l) } catch (_: Throwable) {}
        focusListener = null
    }

    fun stateText(): String = when (state) {
        State.IDLE -> "就绪"
        State.LOADING -> "正在加载音色…"
        State.READING -> "正在朗读"
        State.PAUSED -> "已暂停"
    }

}
