package app.caretreader.tts

// 纯 Kotlin，不依赖 Android：合成循环（逐句合成 → 变调 → 转 16 位 PCM → 插入停顿静音 → 送出）。
// Android 里“送出”就是 SynthesisCallback.audioAvailable；JVM 测试里是写 wav 文件。

/** 一个已加载的模型（真实实现包着 sherpa-onnx OfflineTts）。 */
interface Synthesizer {
    val sampleRate: Int
    /** 合成一个小句，返回 -1..1 的浮点采样。 */
    fun synth(text: String, sid: Int, speed: Float): FloatArray
}

/** 声音输出口。返回 false 表示对方已经停止（例如用户按了停止），应立刻结束。 */
fun interface PcmOut {
    fun write(pcm: ByteArray, offset: Int, length: Int): Boolean
}

class RenderStats {
    var clauses = 0
    var firstAudioMs = -1L
    var synthMs = 0L
    var speechSeconds = 0.0
    var silenceSeconds = 0.0
    var stopped = false
    val rtf: Double get() = if (speechSeconds > 0) synthMs / 1000.0 / speechSeconds else 0.0
}

/**
 * @param speed      送给模型的语速（1.0 = 正常；已经乘上了调用方 App 的语速）
 * @param pitch      音高倍数（1.0 = 不变）。用“先按 speed/pitch 合成，再整体重采样 pitch 倍”实现，
 *                   这样总时长仍然由 speed 决定，只有音高变化。
 * @param pauseScale 停顿时长倍数（调用方 App 让语速变快时停顿相应变短）
 * @param isStopped  每句前后都会检查；Android 里由 onStop() 置位
 */
class SpeechRenderer(
    private val synth: Synthesizer,
    private val sid: Int,
    private val speed: Float,
    private val pitch: Float,
    private val pauseScale: Double,
    private val out: PcmOut,
    private val isStopped: () -> Boolean,
    private val clock: () -> Long = { System.nanoTime() / 1_000_000 },
    /** 音量倍数（个别音色天生偏小声，在 voices.json 里用 gain 补偿；超出范围的部分会被截断）。 */
    private val gain: Float = 1f,
) {
    /** 第一次出声最晚的期望时间：在这之前，如果预计会“第 1 句播完卡一下”，就先多合成一句再一起送出。 */
    var startupBudgetMs = 1000L

    fun render(clauses: List<Clause>): RenderStats {
        val st = RenderStats()
        val sr = synth.sampleRate
        val t0 = clock()
        var held: ByteArray? = null      // 启动缓冲：暂存第 1 句
        var secPerChar = 0.0
        var charsDone = 0
        for ((idx, c) in clauses.withIndex()) {
            if (isStopped()) { st.stopped = true; return st }
            val ts = clock()
            val engineSpeed = (speed / pitch).coerceIn(0.2f, 5f)
            var samples = synthGuarded(c.text, engineSpeed)
            val dt = clock() - ts
            st.synthMs += dt
            if (isStopped()) { st.stopped = true; return st }
            if (pitch != 1f) samples = AudioUtil.resample(samples, pitch.toDouble())
            val speechSec = samples.size.toDouble() / sr
            st.speechSeconds += speechSec
            val pauseSec = c.pauseAfterSeconds * pauseScale
            st.silenceSeconds += pauseSec
            val silence = (pauseSec * sr).toInt().coerceAtLeast(0)
            val pcm = AudioUtil.toPcm16(samples, silence, gain)
            st.clauses++
            charsDone += c.text.length
            secPerChar = st.speechSeconds / charsDone.coerceAtLeast(1)

            if (idx == 0 && clauses.size > 1) {
                // 预测第 2 句要合成多久；如果第 1 句的声音不够撑到那时，且等一等也不会超过 1 秒，就先攒着
                val rtf = st.rtf
                val forecastMs = (clauses[1].text.length * secPerChar * rtf * 1000).toLong()
                val audio1Ms = ((speechSec + pauseSec) * 1000).toLong()
                val elapsed = clock() - t0
                if (audio1Ms < forecastMs + 100 && elapsed + forecastMs <= startupBudgetMs) {
                    held = pcm
                    continue
                }
            }
            if (held != null) {
                if (!emit(held, st, t0)) return st
                held = null
            }
            if (!emit(pcm, st, t0)) return st
        }
        if (held != null) emit(held, st, t0)
        return st
    }

    /** 最后一道保险：送进引擎的文字不超过 80 字（正常情况下 TextSplitter 已经保证了）。 */
    private fun synthGuarded(text: String, engineSpeed: Float): FloatArray {
        if (text.length <= TextSplitter.HARD_MAX_CLAUSE_CHARS) return synth.synth(TextSplitter.textForEngine(text), sid, engineSpeed)
        val parts = ArrayList<FloatArray>()
        var t = text
        while (t.isNotEmpty()) {
            if (isStopped()) break
            val piece: String
            if (t.length > TextSplitter.HARD_MAX_CLAUSE_CHARS) {
                val (h, rest) = TextSplitter.hardCut(t, TextSplitter.MAX_CLAUSE_CHARS); piece = h; t = rest
            } else { piece = t; t = "" }
            parts.add(synth.synth(TextSplitter.textForEngine(piece), sid, engineSpeed))
        }
        val out = FloatArray(parts.sumOf { it.size })
        var o = 0
        for (p in parts) { System.arraycopy(p, 0, out, o, p.size); o += p.size }
        return out
    }

    private fun emit(pcm: ByteArray, st: RenderStats, t0: Long): Boolean {
        if (isStopped()) { st.stopped = true; return false }
        if (st.firstAudioMs < 0) st.firstAudioMs = clock() - t0
        if (!out.write(pcm, 0, pcm.size)) { st.stopped = true; return false }
        return true
    }
}

object AudioUtil {
    private const val KNEE = 0.9f
    /** 浮点 → 16 位小端 PCM，并在后面追加 silenceSamples 个静音采样。超出 [-1,1] 的部分截断，避免爆音。 */
    fun toPcm16(samples: FloatArray, silenceSamples: Int, gain: Float = 1f): ByteArray {
        val out = ByteArray((samples.size + silenceSamples) * 2)
        for (i in samples.indices) {
            var v = samples[i] * gain
            // 与 Windows v0.4+ 的 ApplyGain 相同：超过 0.9 的部分用 tanh 软削波（gain 最大 5，硬截断会爆音）
            if (gain != 1f) {
                val a = Math.abs(v)
                if (a > KNEE) v = Math.signum(v) * (KNEE + (1 - KNEE) * Math.tanh(((a - KNEE) / (1 - KNEE)).toDouble()).toFloat())
            }
            if (v > 1f) v = 1f else if (v < -1f) v = -1f
            val s = (v * 32767f).toInt()
            out[2 * i] = s.toByte()
            out[2 * i + 1] = (s shr 8).toByte()
        }
        return out
    }

    /** 线性插值重采样：输出长度 = 输入 / factor。factor>1 → 音调升高、变短。 */
    fun resample(input: FloatArray, factor: Double): FloatArray {
        if (input.isEmpty() || factor == 1.0) return input
        val n = (input.size / factor).toInt().coerceAtLeast(1)
        val out = FloatArray(n)
        val last = input.size - 1
        for (i in 0 until n) {
            val pos = i * factor
            val k = pos.toInt()
            if (k >= last) { out[i] = input[last]; continue }
            val f = (pos - k).toFloat()
            out[i] = input[k] * (1 - f) + input[k + 1] * f
        }
        return out
    }
}
