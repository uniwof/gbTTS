package app.caretreader.tts

import android.content.Context
import android.content.SharedPreferences
import android.os.Handler
import android.os.Looper
import android.util.Log
import com.k2fsa.sherpa.onnx.OfflineTts
import java.util.concurrent.locks.ReentrantLock
import kotlin.concurrent.thread

const val TAG = "CaretTts"

/** 引擎设置（保存在 SharedPreferences，设置界面和朗读服务共用）。 */
class Prefs(ctx: Context) {
    private val sp: SharedPreferences = ctx.applicationContext.getSharedPreferences("settings", Context.MODE_PRIVATE)

    var voiceId: String?
        get() = sp.getString("voice", null)
        set(v) = sp.edit().putString("voice", v).apply()
    var speed: Float
        get() = sp.getFloat("speed", 1.0f).coerceIn(0.5f, 2.0f)
        set(v) = sp.edit().putFloat("speed", v.coerceIn(0.5f, 2.0f)).apply()
    var threads: Int
        // 不超过手机的 CPU 核数（老手机可能只有 4 核里 2 个大核，开太多线程反而更慢）
        get() = sp.getInt("threads", 2).coerceIn(1, maxThreads())
        set(v) = sp.edit().putInt("threads", v.coerceIn(1, 4)).apply()

    /** 实体键盘快捷键（v0.5，对应 Windows 的全局快捷键）："Ctrl+Alt+R" 这种格式。 */
    var hotkey: String
        get() = sp.getString("hotkey", "Ctrl+Alt+R") ?: "Ctrl+Alt+R"
        set(v) = sp.edit().putString("hotkey", v).apply()
    var stopHotkey: String
        get() = sp.getString("stopHotkey", "Ctrl+Alt+S") ?: "Ctrl+Alt+S"
        set(v) = sp.edit().putString("stopHotkey", v).apply()
    /** 悬浮朗读条（需要开启无障碍服务）。对应 Windows 的“窗口总在最前面”。 */
    var floatingBar: Boolean
        get() = sp.getBoolean("floatingBar", true)
        set(v) = sp.edit().putBoolean("floatingBar", v).apply()
    var barX: Int
        get() = sp.getInt("barX", -1)
        set(v) = sp.edit().putInt("barX", v).apply()
    var barY: Int
        get() = sp.getInt("barY", -1)
        set(v) = sp.edit().putInt("barY", v).apply()

    fun pause(key: String): Float = sp.getFloat("p_$key", DEFAULT_PAUSES.getValue(key)).coerceIn(0f, 3f)
    fun setPause(key: String, v: Float) = sp.edit().putFloat("p_$key", v.coerceIn(0f, 3f)).apply()

    fun pauses() = PauseSettings(
        comma = r(pause("comma")), period = r(pause("period")), semicolon = r(pause("semicolon")),
        colon = r(pause("colon")), newline = r(pause("newline")),
    )

    /** 恢复默认（音色不动）。 */
    fun resetNumbers() {
        val e = sp.edit()
        e.remove("speed").remove("threads").remove("hotkey").remove("stopHotkey")
        for (k in DEFAULT_PAUSES.keys) e.remove("p_$k")
        e.apply()
    }

    private fun r(f: Float) = Math.round(f * 100) / 100.0

    companion object {
        fun maxThreads() = Runtime.getRuntime().availableProcessors().coerceIn(1, 4)
        val DEFAULT_PAUSES = linkedMapOf(
            "comma" to 0.15f, "period" to 0.5f, "semicolon" to 0.3f, "colon" to 0.25f, "newline" to 0.5f,
        )
    }
}

/**
 * 全进程唯一的语音引擎（系统 TTS 服务、光标朗读播放器、试听共用）。
 *  - 同一时间只加载一个模型（换到另一个模型的音色时，先释放旧模型再加载新模型，内存峰值≈一个模型）。
 *  - 同一模型里的不同音色（模型指纹相同）切换不需要重新加载。
 *  - 内置模型直接从 APK 的 assets 里读取；导入的音色从手机存储读取。
 *  - v0.5：多音字前端（与 Windows v0.5 相同的词库 A 版）把汉字换成音色包词典里的私用区字符。
 */
object Engine {
    private val lock = ReentrantLock()
    private var tts: OfflineTts? = null
    private var synth: SherpaSynth? = null
    private var loadedKey: String? = null

    /** 给界面看的状态文字。 */
    @Volatile var status: String = "未加载"
        private set
    /** 正在加载时的大致进度（0~1）；不在加载时为 -1。 */
    @Volatile var loadProgress: Float = -1f
        private set
    private val listeners = ArrayList<() -> Unit>()
    private val main = Handler(Looper.getMainLooper())

    fun catalog(ctx: Context): VoiceCatalog = VoiceLibrary.catalog(ctx)

    fun currentVoice(ctx: Context): VoiceSpec {
        val cat = catalog(ctx)
        return cat.voice(Prefs(ctx).voiceId) ?: cat.defaultVoice
    }

    fun addListener(l: () -> Unit) = synchronized(listeners) { listeners.add(l) }
    fun removeListener(l: () -> Unit) = synchronized(listeners) { listeners.remove(l) }
    private fun notifyListeners() {
        val ls = synchronized(listeners) { listeners.toList() }
        main.post { ls.forEach { it() } }
    }
    private fun setStatus(s: String) { status = s; notifyListeners() }

    /** 在引擎锁内使用指定音色所在的模型（必要时加载）。 */
    fun <T> use(ctx: Context, voice: VoiceSpec, threads: Int, block: (SherpaSynth) -> T): T {
        lock.lock()
        try {
            return block(ensureLoaded(ctx, voice, threads))
        } finally {
            lock.unlock()
        }
    }

    fun isLoaded(voice: VoiceSpec, threads: Int) = loadedKey == "${voice.modelKey}/$threads"

    private val preloadQueued = java.util.concurrent.atomic.AtomicBoolean(false)
    @Volatile private var preloadVoice: VoiceSpec? = null

    /** 后台预加载（设置界面换音色、服务启动时调用），让第一次朗读更快。 */
    fun preloadAsync(ctx: Context, voice: VoiceSpec? = null) {
        val app = ctx.applicationContext
        if (voice != null) preloadVoice = voice
        if (!preloadQueued.compareAndSet(false, true)) return
        thread(name = "tts-preload", isDaemon = true) {
            try {
                lock.lock(); lock.unlock()
                preloadQueued.set(false)
                val v = preloadVoice ?: currentVoice(app)
                preloadVoice = null
                use(app, v, Prefs(app).threads) { }
            } catch (t: Throwable) {
                preloadQueued.set(false)
                Log.e(TAG, "preload failed", t)
            }
        }
    }

    /** 释放模型（正在朗读则不释放）。 */
    fun releaseIfIdle(reason: String) {
        if (Reader.isActive()) return
        if (lock.tryLock()) {
            try {
                if (tts == null) return
                freeLocked()
                setStatus("已释放（$reason）")
            } finally {
                lock.unlock()
            }
        }
    }

    private fun freeLocked() {
        tts?.release()
        tts = null
        synth = null
        loadedKey = null
    }

    private fun ensureLoaded(ctx: Context, voice: VoiceSpec, threads: Int): SherpaSynth {
        val key = "${voice.modelKey}/$threads"
        synth?.let { if (loadedKey == key) return it }

        freeLocked()          // 先释放旧模型，内存里永远只有一个
        setStatus("正在加载 ${voice.name} …")
        val t0 = System.nanoTime()
        // sherpa-onnx 没有加载进度回调：按经验时间估算（和 Windows 版一样），最多走到 95%
        val approx = if (voice.engine == "melo") 4.0 else if (voice.isMatcha) 3.0 else 2.0
        loadProgress = 0f
        val ticker = thread(name = "load-progress", isDaemon = true) {
            try {
                while (loadProgress >= 0f) {
                    val t = (System.nanoTime() - t0) / 1e9
                    loadProgress = minOf(0.95f, (1 - Math.exp(-t / (0.6 * approx))).toFloat())
                    notifyListeners()
                    Thread.sleep(150)
                }
            } catch (_: InterruptedException) {}
        }
        try {
            val app = ctx.applicationContext
            val config = VoiceCatalog.sherpaConfig(voice, threads)
            val t = OfflineTts(assetManager = if (voice.modelInAssets) app.assets else null, config = config)
            val pua = voice.files.puaMap?.let { rel ->
                try {
                    (if (voice.modelInAssets) app.assets.open(voice.path(rel)) else java.io.File(voice.path(rel)).inputStream()).use { PuaMap.parse(it) }
                } catch (e: Throwable) { Log.w(TAG, "pua_map 读取失败", e); null }
            }
            val s = SherpaSynth(t, pua) { Log.i(TAG, it) }
            s.synth("你好", voice.sid, 1.0f)       // 预热
            tts = t; synth = s; loadedKey = key
            val ms = (System.nanoTime() - t0) / 1_000_000
            Log.i(TAG, "loaded ${voice.id} (${voice.modelDir}) threads=$threads in ${ms}ms sr=${s.sampleRate} pua=${pua?.size ?: 0}")
            loadProgress = -1f
            setStatus("已就绪（加载 ${"%.1f".format(ms / 1000.0)} 秒）")
            return s
        } catch (t: Throwable) {
            Log.e(TAG, "load ${voice.id} failed", t)
            freeLocked()
            loadProgress = -1f
            setStatus("加载失败：${t.message ?: t.javaClass.simpleName}")
            throw t
        } finally {
            loadProgress = -1f
            ticker.interrupt()
        }
    }
}

