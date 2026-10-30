package app.caretreader.tts

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import android.speech.tts.TextToSpeech

/** 系统检查“语音数据是否已安装”。模型都打包在 APK 里，所以总是通过。 */
class CheckVoiceData : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // 两个版本都报英文可用（见 CaretTtsService.onIsLanguageAvailable 的说明）
        val available = arrayListOf("zho-CHN", "eng-USA")
        val data = Intent()
            .putStringArrayListExtra(TextToSpeech.Engine.EXTRA_AVAILABLE_VOICES, available)
            .putStringArrayListExtra(TextToSpeech.Engine.EXTRA_UNAVAILABLE_VOICES, arrayListOf())
        setResult(TextToSpeech.Engine.CHECK_VOICE_DATA_PASS, data)
        finish()
    }
}

/** 系统设置里“播放示例”用的示例文字。 */
class GetSampleText : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val lang = intent?.getStringExtra("language") ?: "zho"
        val standard = Engine.catalog(this).voices.any { it.engine == "melo" }
        // 英文示例只有标准版（Melo 中英双语）读得好；轻量版遇到英文请求也给中文示例
        val text = if ((lang == "eng" || lang == "en") && standard)
            "This is an example of speech synthesis in English. 这是光标朗读的语音示例。"
        else
            "这是光标朗读的语音示例。春眠不觉晓，处处闻啼鸟；夜来风雨声，花落知多少。"
        setResult(TextToSpeech.LANG_AVAILABLE, Intent().putExtra(TextToSpeech.Engine.EXTRA_SAMPLE_TEXT, text))
        finish()
    }
}
