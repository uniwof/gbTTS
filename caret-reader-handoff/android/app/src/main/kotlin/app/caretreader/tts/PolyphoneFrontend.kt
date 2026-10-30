package app.caretreader.tts

import java.io.BufferedReader
import java.io.InputStream
import java.io.InputStreamReader

/**
 * v0.5 多音字前端（“新词库 A 版”）—— Windows 版 CaretReader.Core/PolyphoneFrontend.cs 的逐行移植。
 * 纯 Kotlin，不依赖 Android（JVM 测试里与 Windows/Python 版逐字比对）。
 *
 * 定音优先级：① 用户自定义词库 ② 合并词库 A ③ pypinyin 词组/单字 + 上下文规则；jieba 分词；“一/不”与三声变调。
 * 结果通过音色包的 pua_map.txt 把汉字换成“只代表这个拼音”的私用区字符，sherpa-onnx 不用改。
 * 加载完成后只读，可多线程调用。
 */
class PolyphoneFrontend private constructor() {

    private val freq = HashMap<String, Int>(700_000)
    private var total = 0L
    private var logTotal = 0.0
    private val lexA = HashMap<String, String>(400_000)
    private val phrases = HashMap<String, String>(70_000)
    private val phrasePrefix = HashSet<String>(200_000)
    private val chars = HashMap<Int, String>(60_000)
    private val user = LinkedHashMap<String, String>()
    private val start = DoubleArray(4)
    private val trans = Array(4) { DoubleArray(4) }
    private val emit = Array(4) { HashMap<Char, Double>() }

    val userDictWarnings = ArrayList<String>()
    val userWordCount get() = user.size
    var loadSeconds = 0.0
        private set

    companion object {
        const val DATA_DIR = "frontend"
        const val MIN_FLOAT = -3.14e100
        private const val STATES = "BMES"

        /** opener(文件名) → 数据流（assets/frontend/xxx 或磁盘）。userDict = 用户词库全文（可为 null）。 */
        fun load(opener: (String) -> InputStream, userDict: String?): PolyphoneFrontend {
            val t0 = System.nanoTime()
            val f = PolyphoneFrontend()
            f.loadJiebaDict(lines(opener("dict.txt")))
            f.loadHmm(lines(opener("hmm.txt")).toList())
            lines(opener("polylex_a.txt")).forEach { l -> splitWordLine(l)?.let { f.lexA[it.first] = it.second } }
            lines(opener("phrases.txt")).forEach { l ->
                splitWordLine(l)?.let { (w, p) ->
                    f.phrases[w] = p
                    for (k in 1..w.length) f.phrasePrefix.add(w.substring(0, k))
                }
            }
            f.loadChars(lines(opener("char_pinyin.txt")))
            if (userDict != null) f.loadUserDict(userDict.split('\n').map { it.trimEnd('\r') })
            f.loadSeconds = (System.nanoTime() - t0) / 1e9
            return f
        }

        private fun lines(s: InputStream): Sequence<String> {
            val r = BufferedReader(InputStreamReader(s, Charsets.UTF_8), 1 shl 16)
            return generateSequence { r.readLine() ?: run { r.close(); null } }
        }

        private fun splitWordLine(line: String): Pair<String, String>? {
            if (line.isEmpty() || line[0] == '#') return null
            val a = line.indexOf(' ')
            if (a <= 0) return null
            return Pair(line.substring(0, a), line.substring(a + 1))
        }

        private val TONE_VOWELS = arrayOf("āáǎà a", "ēéěè e", "īíǐì i", "ōóǒò o", "ūúǔù u", "ǖǘǚǜ v", "ńňǹ n", "ḿ m")

        /** “háng”/“hang2”/“lü4”/“lv4”/“de”/“de5” → 数字调（轻声 = 5）。识别不了返回 null。 */
        fun toTone3(s0: String): String? {
            var s = s0.trim().lowercase().replace("u:", "v")
            if (s.isEmpty()) return null
            if (s.any { it in '\u0300'..'\u036F' }) {
                var t0 = '\u0000'
                val b = StringBuilder()
                for (ch in s) {
                    when (ch) {
                        '\u0304' -> t0 = '1'
                        '\u0301' -> t0 = '2'
                        '\u030C' -> t0 = '3'
                        '\u0300' -> t0 = '4'
                        '\u0308' -> if (b.isNotEmpty() && b[b.length - 1] == 'u') b.setCharAt(b.length - 1, 'v')
                        else -> { if (ch in '\u0300'..'\u036F') return null; b.append(ch) }
                    }
                }
                s = b.toString() + (if (t0 == '\u0000') "" else t0.toString())
            }
            val sb = StringBuilder()
            var tone = '\u0000'
            var digit = false
            for (c in s) {
                if (c == 'ü') { sb.append('v'); continue }
                var mapped = false
                for (row in TONE_VOWELS) {
                    val sp = row.indexOf(' ')
                    val idx = row.indexOf(c)
                    if (idx in 0 until sp) {
                        val baseCh = row.substring(sp + 1)
                        val t = if (baseCh == "n") idx + 2 else if (baseCh == "m") 2 else idx + 1
                        if (tone != '\u0000') return null
                        tone = '0' + t
                        sb.append(baseCh)
                        mapped = true
                        break
                    }
                }
                if (mapped) continue
                if (c in '1'..'5') { if (tone != '\u0000') return null; tone = c; digit = true; continue }
                if (c in 'a'..'z') { if (digit) return null; sb.append(c); continue }
                return null
            }
            if (sb.isEmpty()) return null
            return sb.toString() + (if (tone == '\u0000') '5' else tone)
        }

        private fun isSegChar(c: Char) = c in '\u3400'..'\u9fff'
        private fun isJiebaHan(c: Char) = c in '\u4E00'..'\u9FD5'
        private fun hz(c: Char) = c in '\u4e00'..'\u9fff'

        /** 和 C#/Python 的 log 一致：用 StrictMath（fdlibm）避免平台差异影响平局判断。 */
        private fun ln(x: Double) = StrictMath.log(x)

        private val PREV_STATUS = arrayOf(intArrayOf(3, 2), intArrayOf(1, 0), intArrayOf(0, 1), intArrayOf(2, 3))
        private const val VERBS = "去来走做说看写买吃干交还付买拿找打学讲想办跑回睡用补花加问"

        // ---------------- 共用实例（后台加载） ----------------
        @Volatile private var shared: PolyphoneFrontend? = null
        @Volatile private var loading: Thread? = null
        @Volatile var lastError: String? = null
            private set
        private val gate = Object()

        fun startLoading(opener: (String) -> InputStream, userDict: () -> String?, log: (String) -> Unit = {}) {
            synchronized(gate) {
                if (shared != null || loading != null) return
                val th = Thread({
                    try {
                        val f = load(opener, userDict())
                        f.toEngineText("银行行长调到总行，一样不对。", PuaMap.EMPTY)
                        lastError = null
                        synchronized(gate) { shared = f }
                        log("多音字前端：加载完成 ${"%.2f".format(f.loadSeconds)}s，用户词 ${f.userWordCount} 个")
                        f.userDictWarnings.forEach { log("用户自定义词库：$it") }
                    } catch (t: Throwable) {
                        // 包括 OutOfMemoryError（内存很小的手机）：按旧方式朗读，不影响使用
                        lastError = t.javaClass.simpleName + ": " + (t.message ?: "")
                        log("多音字前端加载失败，按旧方式朗读：$lastError")
                    } finally {
                        synchronized(gate) { loading = null; (gate as Object).notifyAll() }
                    }
                }, "polyphone-load")
                th.isDaemon = true
                loading = th
                th.start()
            }
        }

        /** 用户改了词库：丢掉旧实例重新加载。 */
        fun reload(opener: (String) -> InputStream, userDict: () -> String?, log: (String) -> Unit = {}) {
            synchronized(gate) {
                while (loading != null) (gate as Object).wait(200)
                shared = null
            }
            startLoading(opener, userDict, log)
        }

        /** 取共用前端；还没加载完就等（最多 timeoutMs）。没有/失败/超时返回 null（按旧方式朗读）。 */
        fun shared(timeoutMs: Long = 5000): PolyphoneFrontend? {
            shared?.let { return it }
            val end = System.currentTimeMillis() + timeoutMs
            synchronized(gate) {
                while (shared == null && loading != null) {
                    val left = end - System.currentTimeMillis()
                    if (left <= 0) break
                    (gate as Object).wait(left)
                }
                return shared
            }
        }

        fun isReady() = shared != null
    }

    // ============================== 加载 ==============================
    private fun loadJiebaDict(ls: Sequence<String>) {
        var t = 0L
        for (line in ls) {
            val a = line.indexOf(' ')
            if (a <= 0) continue
            val b = line.indexOf(' ', a + 1)
            val w = line.substring(0, a)
            val fq = (if (b < 0) line.substring(a + 1) else line.substring(a + 1, b)).toInt()
            freq[w] = fq; t += fq
            for (k in 1 until w.length) {
                val pre = w.substring(0, k)
                if (!freq.containsKey(pre)) freq[pre] = 0
            }
        }
        total = t
    }

    private fun loadHmm(ls: List<String>) {
        val seen = Array(4) { BooleanArray(4) }
        for (line in ls) {
            if (line.isEmpty() || line[0] == '#') continue
            val p = line.split(' ')
            val v = p[p.size - 1].toDouble()
            when (p[0]) {
                "S" -> start[STATES.indexOf(p[1][0])] = v
                "T" -> { val i = STATES.indexOf(p[1][0]); val j = STATES.indexOf(p[1][1]); trans[i][j] = v; seen[i][j] = true }
                "E" -> emit[STATES.indexOf(p[1][0])][p[2][0]] = v
            }
        }
        for (i in 0 until 4) for (j in 0 until 4) if (!seen[i][j]) trans[i][j] = MIN_FLOAT
    }

    private fun loadChars(ls: Sequence<String>) {
        for (line in ls) {
            if (line.isEmpty() || line[0] == '#') continue
            val cp = line.codePointAt(0)
            val sp = line.indexOf(' ')
            if (sp > 0) chars[cp] = line.substring(sp + 1).trim().intern()
        }
    }

    private fun loadUserDict(rawLines: List<String>) {
        var n = 0
        val ws = charArrayOf(' ', '\t', '\u3000')
        for (raw in rawLines) {
            n++
            var line = raw.trimStart('\uFEFF')
            val hash = line.indexOf('#')
            if (hash >= 0) line = line.substring(0, hash)
            line = line.trim()
            if (line.isEmpty()) continue
            val sp = line.indexOfAny(ws)
            if (sp <= 0) { userDictWarnings.add("第${n}行没有拼音，已跳过：$raw"); continue }
            val w = line.substring(0, sp)
            val syl = line.substring(sp + 1).split(' ', '\t', '\u3000').filter { it.isNotEmpty() }
            val outp = ArrayList<String>()
            var ok = true
            for (s in syl) { val t = toTone3(s); if (t == null) { ok = false; break }; outp.add(t) }
            if (!ok) { userDictWarnings.add("第${n}行拼音无法识别，已跳过：$raw"); continue }
            if (outp.size != w.length || w.any { !isSegChar(it) }) {
                userDictWarnings.add("第${n}行字数与拼音数不一致（或含非汉字），已跳过：$raw"); continue
            }
            user[w] = outp.joinToString(" ")
        }
        for (w in user.keys) {
            freq[w] = 10_000_000; total += 10_000_000
            for (k in 1 until w.length) { val pre = w.substring(0, k); if (!freq.containsKey(pre)) freq[pre] = 0 }
        }
        logTotal = 0.0
    }

    // ============================== jieba ==============================
    fun cut(seg: String): List<String> {
        val res = ArrayList<String>()
        var i = 0
        while (i < seg.length) {
            if (isJiebaHan(seg[i])) {
                var j = i
                while (j < seg.length && isJiebaHan(seg[j])) j++
                cutDag(seg.substring(i, j), res)
                i = j
            } else { res.add(seg[i].toString()); i++ }
        }
        return res
    }

    private fun cutDag(s: String, res: MutableList<String>) {
        val n = s.length
        if (logTotal == 0.0) logTotal = ln(total.toDouble())
        val dag = arrayOfNulls<IntArray>(n)
        val tmp = ArrayList<Int>(8)
        for (k in 0 until n) {
            tmp.clear()
            var i = k
            var frag = s.substring(k, k + 1)
            while (i < n) {
                val fq = freq[frag] ?: break
                if (fq != 0) tmp.add(i)
                i++
                if (i < n) frag = s.substring(k, i + 1)
            }
            if (tmp.isEmpty()) tmp.add(k)
            dag[k] = tmp.toIntArray()
        }
        val rp = DoubleArray(n + 1)
        val rx = IntArray(n + 1)
        for (idx in n - 1 downTo 0) {
            var best = Double.NEGATIVE_INFINITY; var bx = -1
            for (x in dag[idx]!!) {
                val fq = freq[s.substring(idx, x + 1)] ?: 0
                val v = ln((if (fq == 0) 1 else fq).toDouble()) - logTotal + rp[x + 1]
                if (v > best || (v == best && x > bx)) { best = v; bx = x }
            }
            rp[idx] = best; rx[idx] = bx
        }
        val buf = StringBuilder()
        fun flush() {
            if (buf.isEmpty()) return
            val b = buf.toString(); buf.setLength(0)
            if (b.length == 1) { res.add(b); return }
            val fq = freq[b]
            if (fq == null || fq == 0) hmmCut(b, res) else for (c in b) res.add(c.toString())
        }
        var p = 0
        while (p < n) {
            val y = rx[p] + 1
            if (y - p == 1) buf.append(s[p]) else { flush(); res.add(s.substring(p, y)) }
            p = y
        }
        flush()
    }

    private fun hmmCut(s: String, res: MutableList<String>) {
        val n = s.length
        val v = Array(n) { DoubleArray(4) }
        val path = Array(n) { IntArray(4) }
        for (y in 0 until 4) v[0][y] = start[y] + (emit[y][s[0]] ?: MIN_FLOAT)
        for (t in 1 until n) for (y in 0 until 4) {
            val em = emit[y][s[t]] ?: MIN_FLOAT
            var best = Double.NEGATIVE_INFINITY; var bs = -1
            for (y0 in PREV_STATUS[y]) {
                val x = v[t - 1][y0] + trans[y0][y] + em
                if (bs < 0 || x > best || (x == best && STATES[y0] > STATES[bs])) { best = x; bs = y0 }
            }
            v[t][y] = best; path[t][y] = bs
        }
        val last = if (v[n - 1][2] > v[n - 1][3] || (v[n - 1][2] == v[n - 1][3] && 'E' > 'S')) 2 else 3
        val pos = IntArray(n)
        pos[n - 1] = last
        for (t in n - 1 downTo 1) pos[t - 1] = path[t][pos[t]]
        var begin = 0; var next = 0
        for (i in 0 until n) {
            when (pos[i]) {
                0 -> begin = i
                2 -> { res.add(s.substring(begin, i + 1)); next = i + 1 }
                3 -> { res.add(s[i].toString()); next = i + 1 }
            }
        }
        if (next < n) res.add(s.substring(next))
    }

    // ============================== pypinyin ==============================
    private fun lazyPinyin(w: String, outp: MutableList<String>) {
        var remain = w
        while (remain.isNotEmpty()) {
            var lastValid = ""; var lastIdx = 0
            var broke = false
            for (index in 0 until remain.length) {
                val word = remain.substring(0, index + 1)
                if (phrasePrefix.contains(word)) {
                    if (phrases.containsKey(word)) { lastValid = word; lastIdx = index + 1 }
                } else {
                    if (lastValid.isNotEmpty()) { phraseOrChars(lastValid, outp); remain = remain.substring(lastIdx) }
                    else { phraseOrChars(remain.substring(0, 1), outp); remain = remain.substring(1) }
                    broke = true
                    break
                }
            }
            if (broke) continue
            if (lastValid.isNotEmpty()) { phraseOrChars(lastValid, outp); remain = remain.substring(lastIdx) }
            else {
                if (!phrases.containsKey(remain)) for (c in remain) phraseOrChars(c.toString(), outp)
                else phraseOrChars(remain, outp)
                break
            }
        }
    }

    private fun phraseOrChars(piece: String, outp: MutableList<String>) {
        val p = phrases[piece]
        if (p != null) { outp.addAll(p.split(' ')); return }
        for (c in piece) outp.add(chars[c.code] ?: (c.toString() + "5"))
    }

    // ============================== 主流程 ==============================
    data class Reading(val index: Int, val ch: String, val pinyin: String)

    fun g2p(text: String): MutableList<Reading> {
        val res = ArrayList<Reading>(text.length)
        var i = 0
        while (i < text.length) {
            if (!isSegChar(text[i])) { i++; continue }
            var j = i
            while (j < text.length && isSegChar(text[j])) j++
            g2pSegment(text.substring(i, j), i, res)
            i = j
        }
        return res
    }

    private fun g2pSegment(seg: String, offset: Int, res: MutableList<Reading>) {
        val pys = ArrayList<String>(seg.length)
        val locked = ArrayList<Boolean>(seg.length)
        for (w in cut(seg)) {
            val lk: Boolean
            val up = user[w]
            val ap = if (up == null && w.length > 1) lexA[w] else null
            if (up != null) { pys.addAll(up.split(' ')); lk = true }
            else if (ap != null) { pys.addAll(ap.split(' ')); lk = true }
            else {
                val before = pys.size
                lazyPinyin(w, pys)
                while (pys.size < before + w.length) pys.add(w[pys.size - before].toString())
                while (pys.size > before + w.length) pys.removeAt(pys.size - 1)
                lk = w.length > 1 && phrases.containsKey(w)
            }
            repeat(w.length) { locked.add(lk) }
        }
        for (k in seg.indices) {
            var p = pys[k]
            if (!locked[k]) p = rules(seg, k, p)
            res.add(Reading(offset + k, seg[k].toString(), p))
        }
    }

    private fun rules(chars: String, i: Int, py: String): String {
        val c = chars[i]
        val prev = if (i > 0) chars[i - 1] else '\u0000'
        val nxtS = if (i + 1 < chars.length) chars[i + 1].toString() else "#"
        val nxt = nxtS[0]
        val nxt2 = chars.substring(minOf(i + 1, chars.length), minOf(i + 1, chars.length) + maxOf(0, minOf(2, chars.length - i - 1)))
        fun inSet(ch: Char, set: String) = ch != '\u0000' && set.indexOf(ch) >= 0
        fun inPrev(set: String) = prev == '\u0000' || set.indexOf(prev) >= 0
        val end = nxtS == "#" || !hz(nxt) || inSet(nxt, "了吧呢吗啊呀")
        when (c) {
            '调' -> { if (inSet(nxt, "走去往任入出换动派职岗档研包头子门号运兵度用查")) return "diao4"; return "tiao2" }
            '还' -> { if (inSet(nxt, "钱书债款给清原手价嘴击贷礼") || end) return "huan2"; return "hai2" }
            '得' -> {
                if (inPrev("你我他她它们就还也都必总") && (inSet(nxt, VERBS) || nxt2 == "赶紧" || nxt2 == "好好" || nxt2 == "马上")) return "dei3"
                if (nxt == '到' || nxt == '分' || nxt == '奖') return "de2"
                if (prev != '\u0000' && hz(prev)) return "de5"
            }
            '着' -> {
                if (inSet(nxt, "急凉火迷魔陆落想手实数")) return "zhao2"
                if (inPrev("睡找猜打点烧够") && (end || nxt == '了')) return "zhao2"
                return "zhe5"
            }
            '背' -> { if (inSet(nxt, "着起上包负带篓走")) return "bei1"; return "bei4" }
            '数' -> if (inSet(nxt, "一钱着到不过清") || nxt == '数') return "shu3"
            '弹' -> if (inSet(nxt, "琴奏唱跳性簧指劾射起开出力幕钢吉乐") || inPrev("会在要能来去再")) return "tan2"
            '朝' -> if (nxt == '阳' && (inPrev("的早晨") || chars.substring(maxOf(0, i - 4), i).contains('早'))) return "zhao1"
            '地' -> if (i > 1 && prev != '\u0000' && prev == chars[i - 2]) return "de5"
            '为' -> if (inSet(nxt, "了他她你我们人大")) return "wei4"
        }
        return py
    }

    fun sandhi(rd: MutableList<Reading>) {
        for (i in rd.indices) {
            val (idx, c, p) = rd[i]
            val nt = if (i + 1 < rd.size) rd[i + 1].pinyin.last().toString() else ""
            if (c == "不" && p == "bu4" && nt == "4") rd[i] = Reading(idx, c, "bu2")
            if (c == "一" && p == "yi1" && i + 1 < rd.size && nt == "4") rd[i] = Reading(idx, c, "yi2")
            else if (c == "一" && p == "yi1" && i + 1 < rd.size && nt.length == 1 && "123".contains(nt) && i > 0 && rd[i - 1].ch != "第")
                rd[i] = Reading(idx, c, "yi4")
        }
        for (i in 0 until rd.size - 1)
            if (rd[i].pinyin.endsWith('3') && rd[i + 1].pinyin.endsWith('3'))
                rd[i] = rd[i].copy(pinyin = rd[i].pinyin.dropLast(1) + "2")
    }

    /** 一个小句 → 送给引擎的文字（汉字换成 PUA 字符；表里没有的拼音保持原字）。 */
    fun toEngineText(text: String, map: PuaMap): String {
        val rd = g2p(text)
        sandhi(rd)
        val sb = StringBuilder(text.length)
        var k = 0
        var i = 0
        while (i < text.length) {
            val c = text[i]
            if (k < rd.size && rd[k].index == i) {
                sb.append(map[rd[k].pinyin] ?: c.toString()); k++; i++; continue
            }
            if (Character.isHighSurrogate(c) && i + 1 < text.length && Character.isLowSurrogate(text[i + 1])) {
                val cp = Character.toCodePoint(c, text[i + 1])
                val py = if (cp in 0x20000..0x3134F) chars[cp] else null
                val pua = if (py != null) map[py] else null
                if (pua != null) sb.append(pua) else sb.append(c).append(text[i + 1])
                i += 2
                continue
            }
            sb.append(c); i++
        }
        return sb.toString()
    }
}

/** 音色包里的 pua_map.txt：一行“拼音 私用区字符”。 */
class PuaMap private constructor(private val m: Map<String, String>) {
    val size get() = m.size
    operator fun get(pinyin: String): String? = m[pinyin]

    companion object {
        val EMPTY = PuaMap(emptyMap())
        fun parse(s: InputStream): PuaMap? {
            val m = HashMap<String, String>()
            BufferedReader(InputStreamReader(s, Charsets.UTF_8)).useLines { ls ->
                for (line in ls) {
                    if (line.isEmpty() || line[0] == '#') continue
                    val sp = line.indexOf(' ')
                    if (sp <= 0 || sp + 1 >= line.length) continue
                    val ch = line.substring(sp + 1).trim()
                    if (ch.length == 1 && ch[0] in '\uE000'..'\uF8FF') m[line.substring(0, sp)] = ch
                }
            }
            return if (m.isNotEmpty()) PuaMap(m) else null
        }
    }
}
