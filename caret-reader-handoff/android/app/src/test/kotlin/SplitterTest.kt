import app.caretreader.tts.*
import java.io.File

// JVM 单元测试：TextSplitter 的 Kotlin 移植版。
//   用法: java -cp ... SplitterTestKt [split_cases.txt] [输出文件]
//   1) 跑和 Windows 版测试程序完全相同的断言（Program.cs SplitTests）
//   2) 按 Windows 版 Dump 的格式输出全部审查用例，和 tests/splitter_output_v3.txt（Windows v0.2 切分器的输出）逐字比对

private var fail = 0
private fun check(name: String, ok: Boolean) { println((if (ok) "  [通过] " else "  [失败] ") + name); if (!ok) fail++ }

private fun fmt(d: Double): String = if (d == Math.floor(d)) d.toLong().toString() else d.toString()

fun main(args: Array<String>) {
    val pz = PauseSettings()
    val sample = "雨停了，街上的灯一盏一盏亮起来。他站在门口，没有进去，也没有离开；" +
        "屋里传来熟悉的笑声：像很多年前一样。\n" +
        "他深吸一口气，抬手敲了敲门！门开了吗？\r\n\r\n" +
        "2024年3月15日，价格是3.14元，共1,000件。Hello, world. This is a test!"
    val cl = TextSplitter.split(sample, pz)
    println("切分结果：")
    for (c in cl) println("  %4.2fs | %s".format(c.pauseAfterSeconds, c.text))
    check("逗号停 0.15", cl[0].text == "雨停了，" && cl[0].pauseAfterSeconds == 0.15)
    check("句号停 0.5", cl[1].text == "街上的灯一盏一盏亮起来。" && cl[1].pauseAfterSeconds == 0.5)
    check("分号停 0.3", cl.any { it.text.endsWith("；") && it.pauseAfterSeconds == 0.3 })
    check("冒号停 0.25", cl.any { it.text.endsWith("：") && it.pauseAfterSeconds == 0.25 })
    check("换行=句号停顿", cl.any { it.text == "像很多年前一样。" && it.pauseAfterSeconds == 0.5 })
    check("！停 0.5", cl.any { it.text == "抬手敲了敲门！" && it.pauseAfterSeconds == 0.5 })
    check("3.14 不被切开", cl.any { it.text.contains("3.14") })
    check("1,000 不被切开", cl.any { it.text.contains("1,000") })
    check("英文逗号", cl.any { it.text == "Hello," && it.pauseAfterSeconds == 0.15 })
    check("英文句号", cl.any { it.text == "world." && it.pauseAfterSeconds == 0.5 })
    check("最后的英文感叹号", cl.last().text == "This is a test!" && cl.last().pauseAfterSeconds == 0.5)

    val a = TextSplitter.split("他说：“好的！”然后走了……", pz)
    check("右引号跟随前句", a.size == 3 && a[1].text == "“好的！”")
    check("省略号=句号", a[2].text == "然后走了……" && a[2].pauseAfterSeconds == 0.5)
    check("纯标点不产生小句", TextSplitter.split("。。，，\n\n   ", pz).isEmpty())
    val d = TextSplitter.split("你好！？\n\n世界", pz)
    check("连续标点+空行只停一次，取最大", d.size == 2 && d[0].pauseAfterSeconds == 0.5)

    val longText = "字".repeat(150)
    val e = TextSplitter.split(longText, pz)
    check("超长无标点：第一句 ≤20 字，其余 ≤36 字，内容不丢",
        e[0].text.length <= TextSplitter.FIRST_CLAUSE_MAX_CHARS && e.all { it.text.length <= TextSplitter.MAX_CLAUSE_CHARS } &&
            e.joinToString("") { it.text } == longText)
    val first = TextSplitter.split("今天下午我们一起去学校旁边的那家新开的书店看看有没有好书，然后回家。", pz)
    println("  首句切分: " + first.joinToString(" | ") { "${it.text}(${fmt(it.pauseAfterSeconds)})" })
    check("首句在 8~20 字的自然断点切开", first[0].text.length in 8..20)
    check("首句切点优先在虚词后（的/了…）", "的了着过和与及或在是就也都而并但把被从向对给让".contains(first[0].text.last()))
    check("切开后原逗号停顿仍在该句末尾", first.any { it.text.endsWith("，") && it.pauseAfterSeconds == 0.15 })
    val em = TextSplitter.split("中".repeat(19) + "😀" + "文".repeat(40), pz)
    check("不切开 emoji（代理对）", em.all { !it.text.last().isHighSurrogate() && !it.text.first().isLowSurrogate() })
    check("不切开数字", TextSplitter.split("中".repeat(18) + "1234567" + "文".repeat(40), pz).any { it.text.contains("1234567") })
    check("不切开英文单词", TextSplitter.split("中".repeat(17) + "internationalization" + "文".repeat(40), pz).any { it.text.contains("internationalization") })
    val engSent = "This is a fairly long English sentence without any punctuation that keeps going on and on for a while"
    val es = TextSplitter.split(engSent, pz)
    println("  英文长句: " + es.joinToString(" | ") { it.text })
    check("英文长句在空格处切，单词完整", es.joinToString(" ") { it.text } == engSent)
    check("全角冒号 10：30 不切", TextSplitter.split("10：30开会", pz).size == 1)
    check("全角逗号 １，０００ 不切", TextSplitter.split("１，０００元", pz).size == 1)
    check("全角句点 ３．１４ 不切", TextSplitter.split("３．１４", pz).size == 1)
    val url = TextSplitter.split("访问https://example.com/a.b?x=1,2吧", pz)
    check("网址里的 ? , : 不切，网址完整", url.size == 1 && url[0].text.contains("https://example.com/a.b?x=1,2"))
    val url2 = TextSplitter.split("看这里 https://a.com/x。然后呢", pz)
    check("网址后的句号仍然生效", url2.size == 2 && url2[0].pauseAfterSeconds == 0.5)
    check("Mr. 后不停顿", TextSplitter.split("我用Mr. Smith的电脑", pz).size == 1)
    check("e.g. 后不停顿", TextSplitter.split("e.g. 例如", pz).size == 1)
    val q = TextSplitter.split("他说:\"好\"", pz)
    check("ASCII 开引号跟随后一句", q.size == 2 && q[1].text == "\"好\"")
    val dash = TextSplitter.split("他——走了", pz)
    check("破折号——短停顿", dash.size == 2 && dash[0].pauseAfterSeconds == 0.15)
    check("间隔号·不停顿", TextSplitter.split("马克·吐温", pz).size == 1)
    // ---------- v0.2：超长 / 读不出来的字符串（与 Windows 版测试相同的断言） ----------
    fun allWithin(l: List<Clause>, max: Int) = l.all { it.text.length <= max }
    fun noBroken(l: List<Clause>) = l.all { it.text.isEmpty() || (!it.text.last().isHighSurrogate() && !it.text.first().isLowSurrogate()) }
    val rnd = java.util.Random(42)
    val hex = (0 until 4000).map { "0123456789abcdef"[rnd.nextInt(16)] }.joinToString("")
    val hx = TextSplitter.split("校验值" + hex + "，请核对。", pz)
    check("4000 位十六进制 → “一串字符”，没有超长小句", hx.any { it.text.contains("一串字符") } && allWithin(hx, 36) && hx.size <= 3)
    val longUrl = "https://example.com/path/" + (0 until 1800).map { 'a' + it % 26 }.joinToString("")
    val lu = TextSplitter.split("请访问" + longUrl + "。谢谢", pz)
    check("长网址 → “链接”，后面的句号停顿保留", lu.size == 2 && lu[0].text == "请访问链接。" && lu[0].pauseAfterSeconds == 0.5)
    check("短网址照常保留", TextSplitter.split("访问https://a.com/x吧", pz)[0].text == "访问https://a.com/x吧")
    val hy = TextSplitter.split("Supercalifragilistic-expialidocious-and-other-very-long-hyphenated-words-hyphens-forever then more", pz)
    check("连字符长串：第一句 ≤20 字（找不到安全切点时硬切）", hy[0].text.length <= 20 && allWithin(hy, 36))
    val hyLong = (0 until 120).joinToString("-") { "word" + ('a' + it % 26) }
    val hl = TextSplitter.split("开头一句话，" + hyLong, pz)
    check("超长连字符串：每段 ≤36 字，内容不丢", allWithin(hl, TextSplitter.MAX_CLAUSE_CHARS) &&
        hl.joinToString("") { it.text }.replace("，", "") == "开头一句话" + hyLong)
    val letters = (0 until 3000).map { 'a' + rnd.nextInt(26) }.joinToString("")
    val lt = TextSplitter.split("密钥" + letters + "结束", pz)
    check("3000 个连续字母（不是单词）→ “一串字符”", lt.size == 1 && lt[0].text == "密钥一串字符结束")
    check("ID / 序列号 → “一串字符”", TextSplitter.split("订单号 A1B2C3D4E5F6G7H8 已发货", pz)[0].text == "订单号 一串字符 已发货")
    check("16 位以上纯数字 → “一串数字”", TextSplitter.split("卡号6222020200112233445。", pz)[0].text == "卡号一串数字。")
    check("普通长单词不替换", TextSplitter.split("internationalization", pz)[0].text == "internationalization")
    check("Base64 → “一串字符”", TextSplitter.split("数据 aGVsbG8gd29ybGQgdGhpcyBpcyBiYXNlNjQ0= 结束", pz)[0].text.contains("一串字符"))
    check("3000 个符号：没有超长小句", allWithin(TextSplitter.split("a" + "=".repeat(3000) + "b", pz), TextSplitter.HARD_MAX_CLAUSE_CHARS))
    val eo = TextSplitter.split("😀中".repeat(300), pz)
    check("emoji 长串：不切开代理对，每段 ≤36", noBroken(eo) && allWithin(eo, 36))
    val pool = "中文字ab12 😀,.-_/=:；"
    val big = StringBuilder()
    repeat(200_000) { val ch = pool[rnd.nextInt(pool.length)]; big.append(if (ch.isSurrogate()) "😀" else ch.toString()) }
    val t0 = System.nanoTime()
    val bg = TextSplitter.split(big.toString(), pz, firstChunk = true, maxChars = 100_000)
    println("  20 万字随机混合文本（截断到 10 万）: ${bg.size} 句，最长 ${bg.maxOf { it.text.length }} 字，用时 ${(System.nanoTime() - t0) / 1_000_000}ms")
    check("随机混合文本：所有小句 ≤80 字，不切开 emoji，截断生效", allWithin(bg, TextSplitter.HARD_MAX_CLAUSE_CHARS) && noBroken(bg) && bg.sumOf { it.text.length } <= 100_000)
    val cont = TextSplitter.split("今天下午我们一起去学校旁边的那家新开的书店看看有没有好书，然后回家。", pz, firstChunk = false)
    check("后续文字（firstChunk=false）第一句不强制缩到 20 字", cont[0].text.length in 21..36)
    // Android 里 TextToSpeech 单次请求最多 4000 字：最坏情况下的切分耗时
    val t1 = System.nanoTime(); TextSplitter.split(big.substring(0, 4000), pz)
    println("  4000 字请求切分用时 ${(System.nanoTime() - t1) / 1_000_000}ms")

    check("引擎文字：10:30 保持", TextSplitter.textForEngine("10:30开会") == "10:30开会")
    check("引擎文字：10：30 保持", TextSplitter.textForEngine("10：30开会") == "10：30开会")
    check("引擎文字：网址保持", TextSplitter.textForEngine("访问https://a.com/x吧") == "访问https://a.com/x吧")
    check("引擎文字：句末 ：；替换为，", TextSplitter.textForEngine("他说：") == "他说，" && TextSplitter.textForEngine("离开；") == "离开，")
    check("引擎文字：英文 ;: 替换为 ,", TextSplitter.textForEngine("note: a; b") == "note, a, b")

    // 自定义停顿值生效
    val custom = TextSplitter.split("甲，乙。丙", PauseSettings(comma = 0.4, period = 1.0))
    check("自定义停顿值生效", custom[0].pauseAfterSeconds == 0.4 && custom[1].pauseAfterSeconds == 1.0)

    // AudioUtil
    val pcm = AudioUtil.toPcm16(floatArrayOf(0f, 1f, -1f, 2f), 3)
    check("PCM：长度含静音", pcm.size == (4 + 3) * 2)
    check("PCM：截断不溢出", pcm[6] == 0xFF.toByte() && pcm[7] == 0x7F.toByte())
    check("重采样 2 倍长度减半", AudioUtil.resample(FloatArray(1000), 2.0).size == 500)

    // 审查用例 dump
    if (args.isNotEmpty()) {
        val sb = StringBuilder()
        for (line in File(args[0]).readLines()) {
            if (line.isEmpty()) continue
            val t = line.replace("\\r", "\r").replace("\\n", "\n")
            sb.append("IN : ").append(line).append('\n')
            for (c in TextSplitter.split(t, PauseSettings()))
                sb.append("   [${c.text}] +${fmt(c.pauseAfterSeconds)}  -> engine: [${TextSplitter.textForEngine(c.text)}]  len=${c.text.length}\n")
        }
        File(args[1]).writeText(sb.toString())
    }
    println(if (fail == 0) "全部通过" else "$fail 项失败")
    if (fail != 0) System.exit(1)
}
