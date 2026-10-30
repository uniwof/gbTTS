package app.caretreader.tts

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import android.widget.Toast

/**
 * 没有界面的入口：
 *  - 选中文字后弹出的菜单里的「光标朗读」（ACTION_PROCESS_TEXT，Android 6+，几乎所有 App 的选中菜单都有）→ 只读选中的文字；
 *  - 任何 App 的“分享”→「光标朗读」（ACTION_SEND 文本）→ 读分享来的文字。
 * 收到文字就交给朗读播放器，自己立刻关闭，不打断正在看的 App。
 */
class ReadTextActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val i = intent
        val (text, label) = when (i?.action) {
            Intent.ACTION_PROCESS_TEXT -> i.getCharSequenceExtra(Intent.EXTRA_PROCESS_TEXT)?.toString() to "朗读选中"
            Intent.ACTION_SEND -> (i.getCharSequenceExtra(Intent.EXTRA_TEXT)?.toString() ?: i.getStringExtra(Intent.EXTRA_SUBJECT)) to "朗读分享的文字"
            else -> null to ""
        }
        if (text.isNullOrBlank()) Toast.makeText(this, "没有收到文字", Toast.LENGTH_SHORT).show()
        else Reader.start(this, text, label)
        finish()
    }
}
