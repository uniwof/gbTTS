package app.caretreader.tts

// 纯 Kotlin，不依赖 Android：可以直接在电脑上的 JVM 里做单元测试。
// 逐行移植自 Windows 版 CaretReader.Core/TextSplitter.cs（v0.2：含“读不出来的长串”处理和硬切保险），
// 规则和测试输出保持逐字一致（见 tests/splitter_output_v3.txt）。

/** 一个“小句”：要朗读的文字 + 读完之后要停顿多少秒。 */
data class Clause(val text: String, val pauseAfterSeconds: Double)

/** 各种标点后面的停顿时长（秒）。默认值和 Windows 版一致（用户试听认可）。 */
data class PauseSettings(
    val comma: Double = 0.15,      // ，、 ,  ——
    val period: Double = 0.50,     // 。！？ . ! ? ……
    val semicolon: Double = 0.30,  // ； ;
    val colon: Double = 0.25,      // ： :
    val newline: Double = 0.50,    // 换行
) {
    fun clamped() = PauseSettings(
        comma.coerceIn(0.0, 3.0), period.coerceIn(0.0, 3.0), semicolon.coerceIn(0.0, 3.0),
        colon.coerceIn(0.0, 3.0), newline.coerceIn(0.0, 3.0),
    )
}

/**
 * 把一大段文字按标点切成小句，并给每个小句配上“停顿时长”。
 *
 *   ，、 ,  ——      → 逗号停顿（默认 0.15 秒）
 *   。！？ . ! ? …… → 句号停顿（默认 0.5 秒）
 *   ； ;            → 分号停顿（默认 0.3 秒）
 *   ： :            → 冒号停顿（默认 0.25 秒）
 *   换行             → 换行停顿（默认 0.5 秒）
 *
 * 不会被切开：数字里的 3.14 / 1,000 / 10:30（含全角）、网址、英文缩写、英文单词。
 * 长度控制：第一句 ≤ 20 字（尽快出声），其余 ≤ 36 字，在自然断点切开；实在没有安全切点才硬切。
 * 读不出来的长串：长网址 → “链接”；哈希 / ID → “一串字符”；16 位以上纯数字 → “一串数字”。
 */
object TextSplitter {
    const val MAX_CLAUSE_CHARS = 36
    const val FIRST_CLAUSE_MAX_CHARS = 20
    const val MIN_PIECE_CHARS = 8
    /** 任何送进引擎的文字的绝对上限（流水线里的最后一道保险）。 */
    const val HARD_MAX_CLAUSE_CHARS = 80
    /** 超过这个长度的网址读作“链接”。 */
    const val LONG_URL_CHARS = MAX_CLAUSE_CHARS
    /** 字母数字串达到这个长度才可能被当作“一串字符”。 */
    const val ID_RUN_CHARS = 16

    private const val CLOSERS = "”’」』）)】]》〉"
    private const val SOFT_BREAK_AFTER = "的了着过和与及或在是就也都而并但把被从向对给让"

    private val ABBREVIATIONS = setOf(
        "mr", "mrs", "ms", "dr", "st", "prof", "sr", "jr", "vs", "etc", "e.g", "i.e", "no", "fig", "approx", "inc", "ltd", "co",
    )

    private val URL_REGEX = Regex(
        "(?:https?://|www\\.)[^\\s\\u3000-\\u303F\\uFF00-\\uFFEF\\u4E00-\\u9FFF\"“”‘’<>]+",
        RegexOption.IGNORE_CASE,
    )

    private val TRIM_ALL = charArrayOf(' ', '\t', '\u3000', '\n')
    private val TRIM_SPACE = charArrayOf(' ', '\t', '\u3000')

    /** firstChunk=false：接在已经在读的内容后面的文字，第一句不需要特别缩短。maxChars>0 时先截断。 */
    fun split(text: String?, pauses: PauseSettings = PauseSettings(), firstChunk: Boolean = true, maxChars: Int = 0): List<Clause> {
        var t = text
        if (t != null && maxChars > 0 && t.length > maxChars) {
            var cut = maxChars
            if (t[cut - 1].isHighSurrogate()) cut--
            t = t.substring(0, cut)
        }
        val raw = splitAtPunctuation(normalizeUnreadable(t), pauses)
        val result = ArrayList<Clause>(raw.size + 4)
        for (orig in raw) {
            var c = orig
            val before = result.size
            if (firstChunk && result.isEmpty() && c.text.length > FIRST_CLAUSE_MAX_CHARS) {
                val (head, tail) = cutOnce(c.text, FIRST_CLAUSE_MAX_CHARS, MAX_CLAUSE_CHARS)
                if (tail.isNotEmpty()) {
                    if (hasSpeakable(head)) result.add(Clause(head, 0.0))
                    c = c.copy(text = tail)
                }
            }
            for (piece in splitLong(c.text, MAX_CLAUSE_CHARS)) if (hasSpeakable(piece)) result.add(Clause(piece, 0.0))
            if (result.size == before) {
                if (result.isNotEmpty() && c.pauseAfterSeconds > result[result.size - 1].pauseAfterSeconds)
                    result[result.size - 1] = result[result.size - 1].copy(pauseAfterSeconds = c.pauseAfterSeconds)
                continue
            }
            result[result.size - 1] = result[result.size - 1].copy(pauseAfterSeconds = c.pauseAfterSeconds)
        }
        return result
    }

    // ---------------- 第零步：把“读不出来”的长字符串换成简短的说法 ----------------
    private val TOKEN_REGEX = Regex("[A-Za-z0-9][A-Za-z0-9_\\-+/=]{15,}")

    fun normalizeUnreadable(input: String?): String {
        if (input.isNullOrEmpty()) return input ?: ""
        val text = URL_REGEX.replace(input) { m ->
            val v = m.value
            var end = v.length
            while (end > 0 && ".,;:!?)".indexOf(v[end - 1]) >= 0) end--
            if (end <= LONG_URL_CHARS) v else "链接" + v.substring(end)
        }
        return TOKEN_REGEX.replace(text) { m ->
            val s = m.range.first
            if (s > 0 && isAsciiLetterOrDigit(text[s - 1])) return@replace m.value
            val v = m.value.trimEnd('-', '_', '+', '/', '=')
            val tail = m.value.substring(v.length)
            val spoken = classifyToken(v)
            if (spoken == null) m.value else spoken + tail
        }
    }

    private fun classifyToken(v: String): String? {
        if (v.length < ID_RUN_CHARS) return null
        var digits = 0; var letters = 0; var transitions = 0; var hex = 0; var seps = 0; var longestWord = 0; var word = 0
        var prevKind = 0
        for (ch in v) {
            val kind = if (ch in '0'..'9') 2 else if (isAsciiLetter(ch)) 1 else 0
            if (kind == 2) digits++ else if (kind == 1) letters++ else seps++
            if (ch in '0'..'9' || ch in 'a'..'f' || ch in 'A'..'F') hex++
            if (kind != 0 && prevKind != 0 && kind != prevKind) transitions++
            if (kind != 0) prevKind = kind
            if (kind == 1) { word++; longestWord = maxOf(longestWord, word) } else word = 0
        }
        if (digits == v.length) return "一串数字"
        if (seps == 0 && hex == v.length && digits > 0) return "一串字符"
        if (digits >= 3 && letters >= 3 && transitions >= 4) return "一串字符"
        if (longestWord >= 32) return "一串字符"
        return null
    }

    private fun isAsciiLetterOrDigit(ch: Char) = isAsciiLetter(ch) || ch in '0'..'9'

    // ---------------- 第一步：按标点切 ----------------
    private fun splitAtPunctuation(input: String?, pauses: PauseSettings): List<Clause> {
        val result = ArrayList<Clause>()
        if (input.isNullOrEmpty()) return result
        val text = input.replace("\r\n", "\n").replace('\r', '\n')

        val inUrl = BooleanArray(text.length)
        for (m in URL_REGEX.findAll(text)) {
            var end = m.range.first + m.value.length
            while (end > m.range.first && ".,;:!?)".indexOf(text[end - 1]) >= 0) end--
            for (k in m.range.first until end) inUrl[k] = true
        }

        val current = StringBuilder()
        var quotes = 0
        var i = 0
        while (i < text.length) {
            val c = text[i]
            if (c == '\n') {
                while (i < text.length && (text[i] == '\n' || text[i] == ' ' || text[i] == '\t' || text[i] == '\u3000')) i++
                flush(result, current, pauses.newline)
                continue
            }
            val p = if (inUrl[i]) -1.0 else pauseFor(text, i, pauses)
            if (p >= 0) {
                var maxPause = p
                current.append(c)
                i++
                while (i < text.length && !inUrl[i]) {
                    val n = text[i]
                    val np = pauseFor(text, i, pauses)
                    if (np >= 0) { maxPause = maxOf(maxPause, np); current.append(n); i++ }
                    else if (CLOSERS.indexOf(n) >= 0) { current.append(n); i++ }
                    else if (n == '"' && quotes % 2 == 1) { quotes++; current.append(n); i++ }
                    else break
                }
                flush(result, current, maxPause)
                continue
            }
            if (c == '"') quotes++
            current.append(c)
            i++
        }
        flush(result, current, 0.0)
        return result
    }

    private fun pauseFor(text: String, i: Int, pz: PauseSettings): Double {
        val c = text[i]
        val prev = if (i > 0) text[i - 1] else '\u0000'
        val next = if (i + 1 < text.length) text[i + 1] else '\u0000'
        val betweenDigits = prev.isDigit() && next.isDigit()
        return when (c) {
            '，' -> if (betweenDigits) -1.0 else pz.comma
            '、', '﹐', '､' -> pz.comma
            '—', '―' -> pz.comma
            '。', '！', '？', '｡', '…', '‥' -> pz.period
            '．' -> if (betweenDigits) -1.0 else pz.period
            '；' -> pz.semicolon
            '：' -> if (betweenDigits) -1.0 else pz.colon
            ',' -> if (betweenDigits) -1.0 else pz.comma
            '.' -> when {
                betweenDigits -> -1.0
                next.isLetterOrDigit() -> -1.0
                isAbbreviation(text, i) -> -1.0
                else -> pz.period
            }
            '!', '?' -> pz.period
            ';' -> pz.semicolon
            ':' -> when {
                betweenDigits -> -1.0
                next == '/' -> -1.0
                else -> pz.colon
            }
            else -> -1.0
        }
    }

    private fun isAbbreviation(text: String, dot: Int): Boolean {
        var s = dot
        while (s > 0 && (isAsciiLetter(text[s - 1]) || text[s - 1] == '.')) s--
        if (s == dot) return false
        val word = text.substring(s, dot).trim('.')
        return word.isNotEmpty() && word.lowercase() in ABBREVIATIONS
    }

    private fun flush(result: MutableList<Clause>, current: StringBuilder, pause: Double) {
        val s = current.toString()
        current.setLength(0)
        addClause(result, s, pause)
    }

    private fun addClause(result: MutableList<Clause>, raw: String, pause: Double) {
        val s = raw.trim(*TRIM_ALL)
        if (hasSpeakable(s)) {
            result.add(Clause(s, pause))
        } else if (result.isNotEmpty()) {
            val last = result[result.size - 1]
            if (pause > last.pauseAfterSeconds) result[result.size - 1] = last.copy(pauseAfterSeconds = pause)
        }
    }

    /** 文字里有没有字母、数字或汉字（即真正能读出声音的内容）。 */
    fun hasSpeakable(s: String): Boolean = s.any { it.isLetterOrDigit() }

    // ---------------- 第二步：长度控制 ----------------
    /** 反复切开直到每段 ≤ maxLen；找不到安全切点就在 maxLen 处硬切。 */
    private fun splitLong(input: String, maxLen: Int): List<String> {
        val out = ArrayList<String>()
        var s = input
        while (s.length > maxLen) {
            val (head, tail) = cutOnce(s, maxLen, maxLen)
            if (tail.isEmpty()) break
            out.add(head)
            s = tail
        }
        if (s.isNotEmpty()) out.add(s)
        return out
    }

    /**
     * 找最好的切点。找不到安全切点时：先往后找（最多到 stretch 字），再往前找，最后在 maxLen 处硬切。
     * 只有 s 不超过 stretch 且无法安全切开时才返回 (s, "")。
     */
    private fun cutOnce(s: String, maxLen: Int, stretch: Int): Pair<String, String> {
        val url = urlMask(s)
        var best = -1
        var bestScore = -1
        val minEdge = 4
        val hi = minOf(maxLen, s.length - minEdge)
        val lo = minOf(MIN_PIECE_CHARS, hi)

        for (c in maxOf(minEdge, lo)..hi) {
            val score = cutScore(s, c, url)
            if (score >= bestScore && score >= 0) { best = c; bestScore = score }
        }
        if (best < 0) {
            for (c in maxOf(hi + 1, minEdge)..minOf(stretch, s.length - minEdge)) {
                if (cutScore(s, c, url) >= 0) { best = c; break }
            }
        }
        if (best < 0) {
            var c = minOf(lo, s.length - minEdge)
            while (c >= minEdge) {
                if (cutScore(s, c, url) >= 0) { best = c; break }
                c--
            }
        }
        if (best > 0 && best < s.length) {
            val head = s.substring(0, best).trimEnd(*TRIM_SPACE)
            val tail = s.substring(best).trimStart(*TRIM_SPACE)
            if (hasSpeakable(head) && hasSpeakable(tail)) return Pair(head, tail)
        }
        if (s.length <= stretch) return Pair(s, "")
        return hardCut(s, maxLen)
    }

    /** 在第 n 个字符处硬切（正好切在 emoji 代理对中间就往前挪一个字符）。 */
    fun hardCut(s: String, n0: Int): Pair<String, String> {
        var n = n0
        if (n >= s.length) return Pair(s, "")
        if (n > 1 && s[n - 1].isHighSurrogate()) n--
        return Pair(s.substring(0, n), s.substring(n))
    }

    /** -1 = 不允许；0 = 可以；1 = 中英文交界；2 = 虚词之后；3 = 空格处。 */
    private fun cutScore(s: String, c: Int, url: BooleanArray): Int {
        val a = s[c - 1]
        val b = s[c]
        if (url[c - 1] && url[c]) return -1
        if (b.isLowSurrogate() || a.isHighSurrogate()) return -1
        if (isWordChar(a) && isWordChar(b)) return -1
        if (isNumSep(a) && c >= 2 && s[c - 2].isDigit() && b.isDigit()) return -1
        if (isNumSep(b) && a.isDigit() && c + 1 < s.length && s[c + 1].isDigit()) return -1
        if (CLOSERS.indexOf(b) >= 0) return -1
        val t = Character.getType(b)
        if (t == Character.NON_SPACING_MARK.toInt() || t == Character.ENCLOSING_MARK.toInt()) return -1

        if (a == ' ' || a == '\t' || a == '\u3000') return 3
        if (SOFT_BREAK_AFTER.indexOf(a) >= 0 && isCjk(b)) return 2
        if (isCjk(a) != isCjk(b)) return 1
        return 0
    }

    private fun urlMask(s: String): BooleanArray {
        val mask = BooleanArray(s.length)
        for (m in URL_REGEX.findAll(s)) for (k in m.range) mask[k] = true
        return mask
    }

    private fun isAsciiLetter(ch: Char) = (ch in 'a'..'z') || (ch in 'A'..'Z')

    private fun isWordChar(ch: Char): Boolean =
        if (ch.code < 0x2E80) (ch.isLetterOrDigit() || ch == '\'' || ch == '-' || ch == '_')
        else (ch in '０'..'９') || (ch in 'Ａ'..'Ｚ') || (ch in 'ａ'..'ｚ')

    private fun isNumSep(ch: Char) = ch == '.' || ch == ',' || ch == ':' || ch == '：' || ch == '，' || ch == '．'

    private fun isCjk(ch: Char): Boolean {
        val x = ch.code
        return (x in 0x4E00..0x9FFF) || (x in 0x3400..0x4DBF) || (x in 0xF900..0xFAFF) || ch.isSurrogate()
    }

    // ---------------- 送进引擎前的文字处理 ----------------
    private val SEMI_COLON_OUTSIDE_DIGITS = Regex("(?<!\\p{Nd})[；：;:]|[；：;:](?!\\p{Nd})")

    /** ；：;: 换成逗号（两个数字之间的 10:30 和网址除外）；停顿长度不受影响。 */
    fun textForEngine(text: String): String {
        val sb = StringBuilder(text.length)
        var pos = 0
        for (m in URL_REGEX.findAll(text)) {
            sb.append(replaceSemi(text.substring(pos, m.range.first)))
            sb.append(m.value)
            pos = m.range.first + m.value.length
        }
        sb.append(replaceSemi(text.substring(pos)))
        return sb.toString()
    }

    private fun replaceSemi(part: String): String =
        SEMI_COLON_OUTSIDE_DIGITS.replace(part) { if (it.value == "；" || it.value == "：") "，" else "," }
}
