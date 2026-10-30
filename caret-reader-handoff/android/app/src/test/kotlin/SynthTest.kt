import app.caretreader.tts.*
import com.k2fsa.sherpa.onnx.OfflineTts
import java.io.File
import java.io.RandomAccessFile

// JVM 集成测试：用 sherpa-onnx 1.13.8 官方 linux-aarch64 JNI（与 Android AAR 同一版本、同一套 C++/JNI 源码）
// 加载 APK 里将要打包的同一份 assets，跑和 Android 服务完全相同的 切分 → 合成 → 停顿 → PCM 流程。
//   用法: SynthTestKt <assets目录> <输出目录> [voiceId ...]

fun rssMb(key: String): Long = File("/proc/self/status").readLines().first { it.startsWith(key) }.filter { it.isDigit() }.toLong() / 1024

fun writeWav(f: File, pcm: ByteArray, sr: Int) {
    RandomAccessFile(f, "rw").use { w ->
        w.setLength(0)
        fun le32(v: Int) = byteArrayOf(v.toByte(), (v shr 8).toByte(), (v shr 16).toByte(), (v shr 24).toByte())
        fun le16(v: Int) = byteArrayOf(v.toByte(), (v shr 8).toByte())
        w.write("RIFF".toByteArray()); w.write(le32(36 + pcm.size)); w.write("WAVEfmt ".toByteArray())
        w.write(le32(16)); w.write(le16(1)); w.write(le16(1)); w.write(le32(sr)); w.write(le32(sr * 2)); w.write(le16(2)); w.write(le16(16))
        w.write("data".toByteArray()); w.write(le32(pcm.size)); w.write(pcm)
    }
}

const val SAMPLE = "雨停了，街上的灯一盏一盏亮起来。他站在门口，没有进去，也没有离开；" +
    "屋里传来熟悉的笑声：像很多年前一样。\n" +
    "他深吸一口气，抬手敲了敲门！门开了吗？\n\n" +
    "2024年3月15日，价格是3.14元，共1,000件。Hello, world. This is a test!"

fun main(args: Array<String>) {
    val assets = File(args[0]).absolutePath
    val outDir = File(args[1]).apply { mkdirs() }
    val only = args.drop(2).toSet()
    var fail = 0
    // ---- 多音字前端（APK 里同一份数据 + 默认用户词库）----
    val fr = PolyphoneFrontend.load({ File("$assets/frontend/$it").inputStream() }, File("$assets/frontend/user_dict_default.txt").readText())
    println("== 多音字前端：加载 ${"%.2f".format(fr.loadSeconds)} s，用户词 ${fr.userWordCount} 个，警告 ${fr.userDictWarnings.size}")
    PolyphoneFrontend.startLoading({ File("$assets/frontend/$it").inputStream() }, { File("$assets/frontend/user_dict_default.txt").readText() })
    // ---- 内置音色：按 APK 里的 index.json 建 VoiceSpec（模型路径换成磁盘路径），并核对“模型指纹”算法 Kotlin == Python ----
    val idx = org.json.JSONObject(File("$assets/voices/index.json").readText())
    val arr = idx.getJSONArray("voices")
    val voices = (0 until arr.length()).map { i ->
        val e = arr.getJSONObject(i)
        val id = e.getString("id"); val mdir = File(assets, e.getString("modelDir"))
        val o = VoiceCatalog.parseManifest(File("$assets/voices/$id/voice.json").readText())
        val v = VoiceCatalog.specFrom(o, id, true, false, mdir.absolutePath, { File(mdir, it).isFile }, { File(mdir, it).length() },
            null, false, null) { files -> VoiceCatalog.modelKey(o.optString("engine"), files, { File(mdir, it).length() }) { r, p, n -> VoiceCatalog.readRangeFile(File(mdir, r), p, n) } }
        val okKey = v.modelKey == e.getString("modelKey")
        if (!okKey) fail++
        println("  [${if (okKey) "通过" else "失败"}] 模型指纹 Kotlin == 打包脚本(Python)：$id")
        v
    }
    // ---- 电脑版音色包文件夹（导入时的格式）也能直接读，指纹与内置一致 → 导入时能认出是同一模型 ----
    val pk = File("/workspace/work/tts/app/pk/voices")
    for (v in voices) {
        val d = File(pk, v.id)
        if (!d.isDirectory) continue
        val dv = VoiceCatalog.loadDiskPack(d) { null }
        val ok = dv.modelKey == v.modelKey && dv.sid == v.sid && dv.name == v.name
        if (!ok) fail++
        println("  [${if (ok) "通过" else "失败"}] 电脑版音色包 ${v.id} 读取成功，指纹与内置相同（导入时只存 voice.json + 样音）")
    }
    var tts: OfflineTts? = null
    var loaded: String? = null
    var last: SherpaSynth? = null
    for (v in voices) {
        if (only.isNotEmpty() && v.id !in only) continue
        if (loaded != v.modelKey) {
            tts?.release(); tts = null
            val t0 = System.nanoTime()
            tts = OfflineTts(assetManager = null, config = VoiceCatalog.sherpaConfig(v, 2))
            loaded = v.modelKey
            println("== 加载 ${v.id} (${File(v.modelDir).name}): ${(System.nanoTime() - t0) / 1_000_000} ms, sr=${tts.sampleRate()}, speakers=${tts.numSpeakers()}, RSS=${rssMb("VmRSS")} MB")
        }
        val pua = v.files.puaMap?.let { File(v.path(it)).inputStream().use { s -> PuaMap.parse(s) } }
        val synth = SherpaSynth(tts!!, pua); last = synth
        val probe = fr.toEngineText("银行行长调到总行，还钱还是还书？", pua ?: PuaMap.EMPTY)
        val nPua = probe.count { it in '\uE000'..'\uF8FF' }
        val okPua = pua != null && nPua >= 12
        if (!okPua) fail++
        println("  [${if (okPua) "通过" else "失败"}] 多音字：${v.id} 的 PUA 表 ${pua?.size ?: 0} 条，示例句 $nPua/16 个字按定好的读音送进引擎")
        val buf = java.io.ByteArrayOutputStream()
        val clauses = TextSplitter.split(SAMPLE, PauseSettings())
        val st = SpeechRenderer(synth, v.sid, v.speed, 1.0f, 1.0, { p, o, l -> buf.write(p, o, l); true }, { false }, gain = v.gain).render(clauses)
        val pcm = buf.toByteArray()
        val f = File(outDir, "android_${v.id}.wav"); writeWav(f, pcm, synth.sampleRate)
        val total = pcm.size / 2.0 / synth.sampleRate
        val okLen = Math.abs(total - (st.speechSeconds + st.silenceSeconds)) < 0.05 && st.silenceSeconds > 3.0
        var peak = 0; for (i in 0 until pcm.size / 2) { val s = (pcm[2*i].toInt() and 0xFF) or (pcm[2*i+1].toInt() shl 8); peak = maxOf(peak, Math.abs(s.toShort().toInt())) }
        val ok = okLen && peak > 3000 && peak < 32767 && st.clauses == clauses.size
        if (!ok) fail++
        println("  [${if (ok) "通过" else "失败"}] ${v.id} sid=${v.sid} gain=${v.gain}: ${st.clauses} 句, 语音 ${"%.2f".format(st.speechSeconds)} s + 停顿 ${"%.2f".format(st.silenceSeconds)} s = ${"%.2f".format(total)} s, " +
            "首次出声 ${st.firstAudioMs} ms, RTF ${"%.2f".format(st.rtf)}, 峰值 $peak, RSS ${rssMb("VmRSS")} MB / 峰 ${rssMb("VmHWM")} MB -> ${f.name}")
        // 多音字示例句单独存一份，方便人耳检查
        val b2 = java.io.ByteArrayOutputStream()
        SpeechRenderer(synth, v.sid, v.speed, 1.0f, 1.0, { p, o, l -> b2.write(p, o, l); true }, { false }, gain = v.gain)
            .render(TextSplitter.split("银行行长调到了总行。我还没还钱，还是先还书吧。他背着包，一边数一数钱。", PauseSettings()))
        writeWav(File(outDir, "android_${v.id}_多音字.wav"), b2.toByteArray(), synth.sampleRate)
    }
    val tts0 = tts!!
    // ---- 停止：写出第一段之后立刻“按停止”，后面不应再合成 ----
    val synth = last!!
    var writes = 0; var stopped = false
    val t1 = System.nanoTime()
    val st = SpeechRenderer(synth, 0, 1f, 1f, 1.0, { _, _, _ -> writes++; stopped = true; true }, { stopped }).render(TextSplitter.split(SAMPLE.repeat(5)))
    val ms = (System.nanoTime() - t1) / 1_000_000
    val okStop = st.stopped && writes <= 2 && st.clauses <= 3
    if (!okStop) fail++
    println("  [${if (okStop) "通过" else "失败"}] 停止：写出 $writes 次后停下，共合成 ${st.clauses} 句（总 ${TextSplitter.split(SAMPLE.repeat(5)).size} 句），用时 $ms ms")
    // ---- 输出口返回 false（Android 里 audioAvailable 返回 ERROR）也要立刻停 ----
    var w2 = 0
    val st2 = SpeechRenderer(synth, 0, 1f, 1f, 1.0, { _, _, _ -> w2++; false }, { false }).render(TextSplitter.split(SAMPLE))
    val okErr = st2.stopped && w2 == 1
    if (!okErr) fail++
    println("  [${if (okErr) "通过" else "失败"}] 输出口报错后立刻停：写 $w2 次")
    // ---- 音高：pitch 1.25 时总时长应与 pitch 1.0 接近（只变音高不变语速） ----
    // VITS 的时长预测带随机性，所以用较长的文字、每种跑 3 次取平均，并且只比较“语音部分”（不含停顿）
    val txt = TextSplitter.split("今天天气很好，我们一起去公园散步吧。路边的花都开了，空气里有淡淡的香味。")
    fun dur(p: Float, sp: Float): Double = (1..3).map {
        SpeechRenderer(synth, 0, sp, p, 1.0, { _, _, _ -> true }, { false }).render(txt).speechSeconds }.average()
    val d1 = dur(1f, 1f); val d2 = dur(1.25f, 1f); val d3 = dur(1f, 1.5f)
    val okPitch = Math.abs(d2 - d1) / d1 < 0.15 && d3 < d1 * 0.85
    if (!okPitch) fail++
    println("  [${if (okPitch) "通过" else "失败"}] 音高/语速：原 ${"%.2f".format(d1)} s，音高×1.25 ${"%.2f".format(d2)} s，语速×1.5 ${"%.2f".format(d3)} s")
    tts0.release()
    println(if (fail == 0) "全部通过" else "$fail 项失败")
    System.exit(if (fail == 0) 0 else 1)
}
