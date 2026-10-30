package app.caretreader.tts

import com.k2fsa.sherpa.onnx.OfflineTts

/**
 * sherpa-onnx OfflineTts → Synthesizer（纯 Kotlin，JVM 测试里用同一份代码）。
 * 有 PUA 表且多音字前端已加载时，先把文字换成定好读音的私用区字符（与 Windows v0.5 TtsEngine.Synthesize 相同）。
 */
class SherpaSynth(val tts: OfflineTts, private val pua: PuaMap?, private val log: (String) -> Unit = {}) : Synthesizer {
    override val sampleRate: Int = tts.sampleRate()
    private var loggedFirst = false
    override fun synth(text: String, sid: Int, speed: Float): FloatArray =
        tts.generate(text = engineText(text), sid = sid, speed = speed).samples

    fun engineText(text: String): String {
        if (pua == null) return text
        val f = PolyphoneFrontend.shared(5000) ?: return text
        return try {
            val r = f.toEngineText(text, pua)
            if (!loggedFirst) { loggedFirst = true; log("多音字前端：已启用（本句 ${r.count { it in '\uE000'..'\uF8FF' }}/${text.length} 个字按定好的读音朗读）") }
            r
        } catch (t: Throwable) { log("多音字前端出错，这一句按原文朗读：$t"); text }
    }
}
