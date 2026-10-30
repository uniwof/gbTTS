package app.caretreader.tts

import android.content.ComponentCallbacks2
import android.media.AudioFormat
import android.speech.tts.SynthesisCallback
import android.speech.tts.SynthesisRequest
import android.speech.tts.TextToSpeech
import android.speech.tts.TextToSpeechService
import android.speech.tts.Voice
import android.util.Log
import java.util.Locale

/**
 * Android 系统 TTS 引擎服务。
 * 出现在 设置 → 文字转语音输出 → 首选引擎 里；微信读书、静读天下等任何使用 Android TextToSpeech 的 App 都能调用。
 *
 * 一次请求：按标点切成小句 → 逐句合成 → 每句后面插入停顿静音 → 立刻通过 SynthesisCallback 送出（边合成边播放）。
 * 停止（onStop）：当前小句合成完后立即丢弃并结束；之后的 audioAvailable 也会返回错误，循环马上退出。
 */
class CaretTtsService : TextToSpeechService() {

    @Volatile private var requestGen = 0L
    @Volatile private var stoppedGen = -1L

    override fun onCreate() {
        super.onCreate()
        App.ensureFrontend(this)
        Engine.preloadAsync(this)
    }

    // ---------------- 语言 ----------------

    override fun onIsLanguageAvailable(lang: String?, country: String?, variant: String?): Int {
        val l = (lang ?: "").lowercase(Locale.ROOT)
        return when {
            l == "zho" || l == "chi" || l == "zh" || l == "cmn" ->
                if ((country ?: "").uppercase(Locale.ROOT) == "CHN") TextToSpeech.LANG_COUNTRY_AVAILABLE else TextToSpeech.LANG_AVAILABLE
            // 英文也报“可用”：标准版的 Melo 是中英双语模型；轻量版用中文模型读（英文字母会逐个读出）。
            // 不这样做的话，系统语言是英文的手机上，系统“文字转语音”页面的“播放/语速/音调”会变灰（真机 Pixel 8 / Android 15 实测），
            // 而且按系统语言调用朗读的 App 会被拒绝。
            l == "eng" || l == "en" ->
                if ((country ?: "").uppercase(Locale.ROOT) == "USA") TextToSpeech.LANG_COUNTRY_AVAILABLE else TextToSpeech.LANG_AVAILABLE
            else -> TextToSpeech.LANG_NOT_SUPPORTED
        }
    }

    override fun onGetLanguage(): Array<String> = arrayOf("zho", "CHN", "")

    override fun onLoadLanguage(lang: String?, country: String?, variant: String?): Int {
        val r = onIsLanguageAvailable(lang, country, variant)
        if (r >= TextToSpeech.LANG_AVAILABLE) Engine.preloadAsync(this)
        return r
    }

    // ---------------- 音色（Android 5+ Voice API） ----------------
    override fun onGetVoices(): MutableList<Voice> {
        val cat = Engine.catalog(this)
        return cat.voices.map {
            Voice(it.name, Locale.SIMPLIFIED_CHINESE, Voice.QUALITY_HIGH, Voice.LATENCY_NORMAL, false, HashSet())
        }.toMutableList()
    }

    override fun onIsValidVoiceName(voiceName: String?): Int =
        if (Engine.catalog(this).voices.any { it.name == voiceName }) TextToSpeech.SUCCESS else TextToSpeech.ERROR

    override fun onLoadVoice(voiceName: String?): Int {
        val ok = onIsValidVoiceName(voiceName)
        // 预加载“调用方要的那个音色”所在的模型（不是设置界面里选的音色），避免先加载错模型再换
        if (ok == TextToSpeech.SUCCESS) Engine.preloadAsync(this, Engine.catalog(this).voices.firstOrNull { it.name == voiceName })
        return ok
    }

    override fun onGetDefaultVoiceNameFor(lang: String?, country: String?, variant: String?): String? {
        if (onIsLanguageAvailable(lang, country, variant) < TextToSpeech.LANG_AVAILABLE) return null
        val l = (lang ?: "").lowercase(Locale.ROOT)
        // 英文只有 Melo（中英双语）读得好：要英文时默认给 Melo，而不是当前选的纯中文音色
        if (l == "eng" || l == "en") Engine.catalog(this).voices.firstOrNull { it.engine == "melo" }?.let { return it.name }
        return Engine.currentVoice(this).name
    }

    // ---------------- 停止 ----------------
    override fun onStop() {
        stoppedGen = requestGen
    }

    override fun onTrimMemory(level: Int) {
        super.onTrimMemory(level)
        @Suppress("DEPRECATION")
        if (level >= ComponentCallbacks2.TRIM_MEMORY_COMPLETE) Engine.releaseIfIdle("内存紧张")
    }

    /** 所有阅读 App 都断开后释放模型，低内存手机不长期占用几百 MB；下次朗读会重新加载（几秒）。 */
    override fun onDestroy() {
        Engine.releaseIfIdle("朗读服务已关闭")
        super.onDestroy()
    }

    // ---------------- 合成 ----------------
    override fun onSynthesizeText(request: SynthesisRequest?, callback: SynthesisCallback?) {
        if (request == null || callback == null) return
        val myGen = ++requestGen
        val isStopped = { stoppedGen >= myGen }
        val text = request.charSequenceText?.toString() ?: ""
        var started = false
        try {
            val prefs = Prefs(this)
            val cat = Engine.catalog(this)
            // 调用方 App 指定了我们的某个音色就用它，否则用设置界面里选的音色
            val voice = cat.voices.firstOrNull { it.name == request.voiceName } ?: Engine.currentVoice(this)

            // 语速、音高：100 = 正常。调用方 App 的语速乘在“基础语速”上；停顿随 App 语速同比例缩短/拉长。
            val appRate = if (request.speechRate > 0) request.speechRate / 100f else 1f
            val speed = (prefs.speed * voice.speed * appRate).coerceIn(0.3f, 4.0f)
            val pitch = if (request.pitch > 0) (request.pitch / 100f).coerceIn(0.5f, 2.0f) else 1f
            val pauseScale = 1.0 / appRate.coerceIn(0.5f, 3.0f)
            val clauses = TextSplitter.split(text, prefs.pauses())

            // 没有可读的字（空串、只有标点/空白，阅读 App 常发 “……” 这种）：不加载模型，直接报告“读完”，
            // 否则模型没加载时要白等几秒，App 的朗读会卡住。
            if (clauses.isEmpty()) {
                if (!isStopped() && callback.start(16000, AudioFormat.ENCODING_PCM_16BIT, 1) == TextToSpeech.SUCCESS) callback.done()
                return
            }

            Engine.use(this, voice, prefs.threads) { synth ->
                if (isStopped()) return@use
                if (callback.start(synth.sampleRate, AudioFormat.ENCODING_PCM_16BIT, 1) != TextToSpeech.SUCCESS) {
                    // 不是被停止、而是 start 本身失败：必须 done()，框架才会把 onError 发给阅读 App，不然 App 一直等
                    if (!isStopped()) callback.done()
                    return@use
                }
                started = true
                val maxBuf = callback.maxBufferSize.coerceAtLeast(2)
                val out = PcmOut { pcm, off, len ->
                    var o = off
                    val end = off + len
                    while (o < end) {
                        if (isStopped()) return@PcmOut false
                        val n = minOf(maxBuf - (maxBuf and 1), end - o)
                        if (callback.audioAvailable(pcm, o, n) != TextToSpeech.SUCCESS) return@PcmOut false
                        o += n
                    }
                    true
                }
                val st = SpeechRenderer(synth, voice.sid, speed, pitch, pauseScale, out, isStopped, gain = voice.gain).render(clauses)
                Log.d(TAG, "req chars=${text.length} clauses=${st.clauses} first=${st.firstAudioMs}ms rtf=${"%.2f".format(st.rtf)} stopped=${st.stopped}")
                if (!st.stopped) callback.done()
            }
        } catch (t: Throwable) {
            // 例如模型加载失败、内存不足。框架只在 start() 之后才会自动补 done()；
            // start() 之前出错时只调 error() 阅读 App 收不到任何回调、会一直卡着等，所以这里再调一次 done()（它会把错误发给 App）。
            Log.e(TAG, "synthesis failed", t)
            try { callback.error() } catch (_: Throwable) {}
            if (!started) try { callback.done() } catch (_: Throwable) {}
        }
    }
}
