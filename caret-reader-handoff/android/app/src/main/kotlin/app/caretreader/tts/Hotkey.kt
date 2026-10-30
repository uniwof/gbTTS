package app.caretreader.tts

import android.view.KeyEvent

/**
 * 实体键盘快捷键（蓝牙/USB 键盘、平板键盘盖）。格式和 Windows 版一样："Ctrl+Alt+R"。
 * 只有开启了无障碍服务才能在任何 App 里生效（系统把按键先交给无障碍服务过滤）。
 */
data class Hotkey(val ctrl: Boolean, val alt: Boolean, val shift: Boolean, val meta: Boolean, val keyCode: Int) {
    val modifierCount get() = listOf(ctrl, alt, shift, meta).count { it }

    fun matches(e: KeyEvent): Boolean =
        e.keyCode == keyCode && e.isCtrlPressed == ctrl && e.isAltPressed == alt && e.isShiftPressed == shift && e.isMetaPressed == meta

    override fun toString(): String {
        val p = ArrayList<String>()
        if (ctrl) p.add("Ctrl"); if (alt) p.add("Alt"); if (shift) p.add("Shift"); if (meta) p.add("Win")
        p.add(keyName(keyCode))
        return p.joinToString("+")
    }

    companion object {
        private val NAMES = mapOf(
            KeyEvent.KEYCODE_SPACE to "Space", KeyEvent.KEYCODE_ENTER to "Enter", KeyEvent.KEYCODE_TAB to "Tab",
            KeyEvent.KEYCODE_ESCAPE to "Esc", KeyEvent.KEYCODE_DEL to "Back", KeyEvent.KEYCODE_FORWARD_DEL to "Del",
            KeyEvent.KEYCODE_INSERT to "Insert", KeyEvent.KEYCODE_MOVE_HOME to "Home", KeyEvent.KEYCODE_MOVE_END to "End",
            KeyEvent.KEYCODE_PAGE_UP to "PgUp", KeyEvent.KEYCODE_PAGE_DOWN to "PgDn",
            KeyEvent.KEYCODE_DPAD_LEFT to "Left", KeyEvent.KEYCODE_DPAD_RIGHT to "Right",
            KeyEvent.KEYCODE_DPAD_UP to "Up", KeyEvent.KEYCODE_DPAD_DOWN to "Down",
        )

        fun keyName(code: Int): String {
            NAMES[code]?.let { return it }
            if (code in KeyEvent.KEYCODE_A..KeyEvent.KEYCODE_Z) return ('A' + (code - KeyEvent.KEYCODE_A)).toString()
            if (code in KeyEvent.KEYCODE_0..KeyEvent.KEYCODE_9) return ('0' + (code - KeyEvent.KEYCODE_0)).toString()
            if (code in KeyEvent.KEYCODE_F1..KeyEvent.KEYCODE_F12) return "F" + (code - KeyEvent.KEYCODE_F1 + 1)
            return KeyEvent.keyCodeToString(code).removePrefix("KEYCODE_")
        }

        private fun codeOf(name: String): Int? {
            val n = name.trim()
            NAMES.entries.firstOrNull { it.value.equals(n, true) }?.let { return it.key }
            if (n.length == 1 && n[0].uppercaseChar() in 'A'..'Z') return KeyEvent.KEYCODE_A + (n[0].uppercaseChar() - 'A')
            if (n.length == 1 && n[0] in '0'..'9') return KeyEvent.KEYCODE_0 + (n[0] - '0')
            if (n.length in 2..3 && (n[0] == 'F' || n[0] == 'f')) n.substring(1).toIntOrNull()?.let { if (it in 1..12) return KeyEvent.KEYCODE_F1 + it - 1 }
            val c = KeyEvent.keyCodeFromString("KEYCODE_" + n.uppercase())
            return if (c > 0) c else null
        }

        fun parse(s: String?): Hotkey? {
            if (s.isNullOrBlank()) return null
            var ctrl = false; var alt = false; var shift = false; var meta = false; var key: Int? = null
            for (p in s.split('+').map { it.trim() }.filter { it.isNotEmpty() }) {
                when (p.lowercase()) {
                    "ctrl", "control" -> ctrl = true
                    "alt" -> alt = true
                    "shift" -> shift = true
                    "win", "meta", "cmd" -> meta = true
                    else -> key = codeOf(p) ?: return null
                }
            }
            return Hotkey(ctrl, alt, shift, meta, key ?: return null)
        }

        fun isModifier(code: Int) = code in setOf(
            KeyEvent.KEYCODE_CTRL_LEFT, KeyEvent.KEYCODE_CTRL_RIGHT, KeyEvent.KEYCODE_ALT_LEFT, KeyEvent.KEYCODE_ALT_RIGHT,
            KeyEvent.KEYCODE_SHIFT_LEFT, KeyEvent.KEYCODE_SHIFT_RIGHT, KeyEvent.KEYCODE_META_LEFT, KeyEvent.KEYCODE_META_RIGHT,
            KeyEvent.KEYCODE_FUNCTION, KeyEvent.KEYCODE_CAPS_LOCK, KeyEvent.KEYCODE_NUM_LOCK, KeyEvent.KEYCODE_SCROLL_LOCK,
        )

        fun from(e: KeyEvent) = Hotkey(e.isCtrlPressed, e.isAltPressed, e.isShiftPressed, e.isMetaPressed, e.keyCode)

        /**
         * 不能用的组合（和 Windows 版 Hotkey.Problem 同一套规则）：复制/粘贴/撤销等常用键、编辑和光标移动键、
         * 少于两个修饰键的字母/数字/符号键。返回中文原因；没问题返回 null。
         */
        fun problem(h: Hotkey, other: Hotkey?): String? {
            val k = h.keyCode
            val name = h.toString()
            if (h.modifierCount == 0 && k !in KeyEvent.KEYCODE_F1..KeyEvent.KEYCODE_F12) return "$name 是普通按键，打字时会误触，请至少加两个修饰键（如 Ctrl+Alt）"
            val onlyCtrl = h.ctrl && !h.alt && !h.shift && !h.meta
            val onlyAlt = h.alt && !h.ctrl && !h.shift && !h.meta
            if (onlyCtrl && k in listOf(KeyEvent.KEYCODE_C, KeyEvent.KEYCODE_V, KeyEvent.KEYCODE_X, KeyEvent.KEYCODE_Z, KeyEvent.KEYCODE_Y,
                    KeyEvent.KEYCODE_A, KeyEvent.KEYCODE_S, KeyEvent.KEYCODE_INSERT, KeyEvent.KEYCODE_ESCAPE)) return "$name 是常用的系统/编辑快捷键"
            if (onlyAlt && k in listOf(KeyEvent.KEYCODE_F4, KeyEvent.KEYCODE_TAB, KeyEvent.KEYCODE_ESCAPE, KeyEvent.KEYCODE_SPACE, KeyEvent.KEYCODE_ENTER)) return "$name 是系统快捷键"
            if (h.ctrl && h.alt && !h.shift && !h.meta && k in listOf(KeyEvent.KEYCODE_FORWARD_DEL, KeyEvent.KEYCODE_ESCAPE, KeyEvent.KEYCODE_TAB)) return "$name 是系统快捷键"
            if (h.ctrl && h.shift && !h.alt && !h.meta && k == KeyEvent.KEYCODE_ESCAPE) return "$name 是系统快捷键"
            val editing = listOf(KeyEvent.KEYCODE_MOVE_END, KeyEvent.KEYCODE_MOVE_HOME, KeyEvent.KEYCODE_DPAD_LEFT, KeyEvent.KEYCODE_DPAD_RIGHT,
                KeyEvent.KEYCODE_DPAD_UP, KeyEvent.KEYCODE_DPAD_DOWN, KeyEvent.KEYCODE_PAGE_UP, KeyEvent.KEYCODE_PAGE_DOWN,
                KeyEvent.KEYCODE_FORWARD_DEL, KeyEvent.KEYCODE_DEL, KeyEvent.KEYCODE_ENTER, KeyEvent.KEYCODE_TAB, KeyEvent.KEYCODE_SPACE)
            if (k in editing && !h.alt && !h.meta) return "$name 是编辑/移动光标用的键"
            val printable = k in KeyEvent.KEYCODE_A..KeyEvent.KEYCODE_Z || k in KeyEvent.KEYCODE_0..KeyEvent.KEYCODE_9 ||
                k in KeyEvent.KEYCODE_GRAVE..KeyEvent.KEYCODE_AT || k == KeyEvent.KEYCODE_SPACE
            if (printable && h.modifierCount < 2) return "$name 只有一个修饰键，容易和其它 App 的快捷键冲突，请用两个（如 Ctrl+Alt+${keyName(k)}）"
            if (other != null && other == h) return "开始/暂停 和 停止 不能用同一个快捷键"
            return null
        }
    }
}
