package app.caretreader.tts

import com.k2fsa.sherpa.onnx.OfflineTtsConfig
import com.k2fsa.sherpa.onnx.OfflineTtsMatchaModelConfig
import com.k2fsa.sherpa.onnx.OfflineTtsModelConfig
import com.k2fsa.sherpa.onnx.OfflineTtsVitsModelConfig
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.InputStream
import java.security.MessageDigest

// v0.5：音色包（与 Windows v0.4/v0.5 的 voices\<id>\voice.json 格式相同）。
// 不依赖 Android 界面类，JVM 测试里也能用。
//  - 内置音色：APK assets/voices/<id>/(voice.json, sample.mp3)，模型文件在 assets/models/<模型>/（同一模型的多个音色共用一份）。
//  - 导入的音色：<应用私有目录>/voices/<id>/ 或 Android/data/<包名>/files/voices/<id>/，一个文件夹 = 一个音色。
//    如果导入的音色和内置音色是同一个模型，只保存 voice.json + 试听（文件夹里放 builtin_model.txt 指向内置模型），不重复占 120 MB。

class VoiceFiles(
    val model: String, val tokens: String, val lexicon: String?, val vocoder: String?,
    val ruleFsts: List<String>, val puaMap: String?,
)

class VoiceSpec(
    val id: String,
    val name: String,
    val engine: String,          // vits | melo | piper | matcha
    val sid: Int,
    val gain: Float,
    val speed: Float,
    val description: String,
    val order: Int,
    /** 模型文件在 assets 里（true，路径相对 assets）还是在磁盘上（false，绝对路径）。 */
    val modelInAssets: Boolean,
    /** 模型文件所在目录（assets 相对路径或绝对路径）。 */
    val modelDir: String,
    val files: VoiceFiles,
    /** 试听文件：assets 路径（builtIn）或绝对路径；null = 没有试听。 */
    val sample: String?,
    val sampleInAssets: Boolean,
    /** 内置音色（随 APK 安装，不能删除）。 */
    val builtIn: Boolean,
    /** 导入的音色在磁盘上的文件夹（删除用）；内置为 null。 */
    val packDir: File?,
    /** 模型指纹：相同 = 同一个模型，切换音色不用重新加载。 */
    val modelKey: String,
) {
    val isMatcha get() = engine == "matcha"
    fun path(rel: String) = "$modelDir/$rel"
    override fun toString() = name
}

class VoiceCatalog(val edition: String, val voices: List<VoiceSpec>, val problems: List<String>) {
    fun voice(id: String?): VoiceSpec? = voices.firstOrNull { it.id.equals(id, ignoreCase = true) }
    val defaultVoice: VoiceSpec get() = voices[0]

    companion object {
        val ENGINES = listOf("vits", "melo", "piper", "matcha")
        const val MANIFEST = "voice.json"
        const val BUILTIN_LINK = "builtin_model.txt"

        /** 生成 sherpa-onnx 配置。 */
        fun sherpaConfig(v: VoiceSpec, threads: Int): OfflineTtsConfig {
            val f = v.files
            val model = if (v.isMatcha) {
                OfflineTtsModelConfig(
                    matcha = OfflineTtsMatchaModelConfig(
                        acousticModel = v.path(f.model), vocoder = v.path(f.vocoder ?: ""),
                        lexicon = f.lexicon?.let { v.path(it) } ?: "", tokens = v.path(f.tokens),
                    ),
                    numThreads = threads, debug = false, provider = "cpu",
                )
            } else {
                OfflineTtsModelConfig(
                    vits = OfflineTtsVitsModelConfig(model = v.path(f.model), lexicon = f.lexicon?.let { v.path(it) } ?: "", tokens = v.path(f.tokens)),
                    numThreads = threads, debug = false, provider = "cpu",
                )
            }
            return OfflineTtsConfig(model = model, ruleFsts = f.ruleFsts.joinToString(",") { v.path(it) }, maxNumSentences = 1)
        }

        // ------------------------------------------------------------------
        //  voice.json（宽松读取：注释、尾逗号、数字写成字符串、键名大小写都可以）
        // ------------------------------------------------------------------
        fun parseManifest(text: String): JSONObject {
            if (text.length > 256 * 1024) throw IllegalArgumentException("voice.json 太大（不像音色说明文件）")
            var t = text.trimStart('\uFEFF')
            t = stripComments(t).replace(Regex(",\\s*([}\\]])"), "$1")
            return lowerKeys(JSONObject(t))
        }

        private fun stripComments(s: String): String {
            val sb = StringBuilder(s.length)
            var i = 0; var inStr = false
            while (i < s.length) {
                val c = s[i]
                if (inStr) {
                    sb.append(c)
                    if (c == '\\' && i + 1 < s.length) { sb.append(s[i + 1]); i += 2; continue }
                    if (c == '"') inStr = false
                    i++; continue
                }
                if (c == '"') { inStr = true; sb.append(c); i++; continue }
                if (c == '/' && i + 1 < s.length && s[i + 1] == '/') { while (i < s.length && s[i] != '\n') i++; continue }
                if (c == '/' && i + 1 < s.length && s[i + 1] == '*') {
                    val e = s.indexOf("*/", i + 2)
                    if (e < 0) throw IllegalArgumentException("voice.json 里的 /* 注释没有结束（缺少 */）")
                    i = e + 2; continue
                }
                sb.append(c); i++
            }
            return sb.toString()
        }

        private fun lowerKeys(o: JSONObject): JSONObject {
            val r = JSONObject()
            val it = o.keys()
            while (it.hasNext()) {
                val k = it.next()
                val v = o.get(k)
                r.put(k.lowercase(), if (v is JSONObject) lowerKeys(v) else v)
            }
            return r
        }

        private fun strList(a: JSONArray?) = if (a == null) emptyList() else (0 until a.length()).map { a.optString(it, "") }.filter { it.isNotBlank() }

        /** 相对路径必须留在包里面（不允许 /开头、..）。 */
        fun safeRel(rel: String?): Boolean {
            if (rel.isNullOrBlank()) return false
            if (rel.startsWith("/") || rel.startsWith("\\") || rel.contains(':')) return false
            return rel.replace('\\', '/').split('/').none { it == ".." }
        }

        /**
         * 读一个音色包。exists(rel) / size(rel) 由调用方提供（assets 或磁盘）。成功返回 VoiceSpec，失败抛 IllegalArgumentException（中文原因）。
         */
        fun specFrom(
            o: JSONObject, id: String, builtIn: Boolean, modelInAssets: Boolean, modelDir: String,
            exists: (String) -> Boolean, size: (String) -> Long, sample: String?, sampleInAssets: Boolean,
            packDir: File?, keyOf: (VoiceFiles) -> String,
        ): VoiceSpec {
            val engine = o.optString("engine", "vits").trim().lowercase()
            if (engine !in ENGINES) throw IllegalArgumentException("不支持的引擎类型「$engine」（只支持 vits / melo / piper / matcha）")
            val f = o.optJSONObject("files") ?: throw IllegalArgumentException("voice.json 里缺少 files")
            val model = f.optString("model", "model.onnx")
            val tokens = f.optString("tokens", "tokens.txt")
            val lexicon = f.optString("lexicon", "").ifBlank { null }
            val vocoder = f.optString("vocoder", "").ifBlank { null }
            val pua = f.optString("puamap", "").ifBlank { null }
            if (model.isBlank() || tokens.isBlank()) throw IllegalArgumentException("voice.json 里缺少 files.model 或 files.tokens")
            if (engine == "matcha" && vocoder == null) throw IllegalArgumentException("matcha 音色缺少 files.vocoder（声码器）")
            val sid = o.optInt("sid", 0)
            if (sid < 0) throw IllegalArgumentException("sid 不能是负数")
            val missing = ArrayList<String>()
            fun need(rel: String?, required: Boolean) {
                if (rel == null) { if (required) missing.add("(未填写)"); return }
                if (!safeRel(rel)) { missing.add("$rel（必须在音色包文件夹里）"); return }
                if (!exists(rel)) missing.add(rel)
            }
            need(model, true); need(tokens, true); need(lexicon, false)
            if (engine == "matcha") need(vocoder, true)
            if (missing.isNotEmpty()) throw IllegalArgumentException("缺少文件：" + missing.joinToString("，"))
            val msize = size(model)
            if (msize < 1024) throw IllegalArgumentException("模型文件太小，可能没有下载完整：$model")
            val mb = o.optLong("modelbytes", 0)
            if (mb > 0 && msize != mb) throw IllegalArgumentException("模型文件不完整（应为 $mb 字节，实际 $msize 字节），请重新下载或解压")
            val fsts = strList(f.optJSONArray("rulefsts")).filter { safeRel(it) && exists(it) }
            val puaOk = if (pua != null && safeRel(pua) && exists(pua)) pua else null
            val files = VoiceFiles(model, tokens, lexicon, vocoder, fsts, puaOk)
            fun d(k: String, def: Double): Double { val v = o.optDouble(k, def); return if (v.isNaN() || v <= 0) def else v }
            var name = o.optString("name", "").trim().ifEmpty { id }
            if (name.length > 40) name = name.substring(0, 40)
            return VoiceSpec(
                id = id, name = name, engine = engine, sid = sid,
                gain = d("gain", 1.0).coerceIn(0.1, 5.0).toFloat(), speed = d("speed", 1.0).coerceIn(0.5, 2.0).toFloat(),
                description = o.optString("description", ""), order = o.optInt("order", 100),
                modelInAssets = modelInAssets, modelDir = modelDir, files = files,
                sample = sample, sampleInAssets = sampleInAssets, builtIn = builtIn, packDir = packDir,
                modelKey = keyOf(files),
            )
        }

        /** 模型指纹：引擎类型 + 大小 + 模型开头/中间/结尾各 1MB 的 SHA-256（与 Windows 版同一思路）。 */
        fun modelKey(engine: String, files: VoiceFiles, size: (String) -> Long, readRange: (String, Long, Int) -> ByteArray): String {
            val len = size(files.model)
            val sha = MessageDigest.getInstance("SHA-256")
            val chunk = 1 shl 20
            for (pos in linkedSetOf(0L, maxOf(0L, len / 2 - chunk / 2), maxOf(0L, len - chunk))) sha.update(readRange(files.model, pos, chunk))
            val h = sha.digest().joinToString("") { "%02x".format(it) }.substring(0, 24)
            fun sz(r: String?) = if (r == null) "-" else size(r).toString()
            val kind = if (engine == "matcha") "matcha" else "vits"
            return "$kind|$len|$h|${sz(files.tokens)}|${sz(files.lexicon)}|${if (engine == "matcha") sz(files.vocoder) else ""}|${files.ruleFsts.joinToString(",")}|${sz(files.puaMap)}"
        }

        fun readRangeFile(f: File, pos: Long, n: Int): ByteArray {
            java.io.RandomAccessFile(f, "r").use { r ->
                if (pos >= r.length()) return ByteArray(0)
                r.seek(pos)
                val b = ByteArray(minOf(n.toLong(), r.length() - pos).toInt())
                r.readFully(b)
                return b
            }
        }

        fun readRangeStream(open: () -> InputStream, pos: Long, n: Int): ByteArray = open().use { s ->
            var skip = pos
            while (skip > 0) { val k = s.skip(skip); if (k <= 0) break; skip -= k }
            val b = ByteArray(n); var got = 0
            while (got < n) { val k = s.read(b, got, n - got); if (k <= 0) break; got += k }
            b.copyOf(got)
        }

        /** 磁盘上的一个音色包文件夹 → VoiceSpec。builtinResolver(模型目录名) 用于 builtin_model.txt。 */
        fun loadDiskPack(dir: File, builtinLookup: (String) -> VoiceSpec?): VoiceSpec {
            val mf = File(dir, MANIFEST)
            if (!mf.isFile) throw IllegalArgumentException("缺少 voice.json")
            val o = parseManifest(mf.readText(Charsets.UTF_8))
            val sampleRel = o.optString("sample", "sample.mp3")
            val sample = if (safeRel(sampleRel) && File(dir, sampleRel).isFile) File(dir, sampleRel).absolutePath else null
            val link = File(dir, BUILTIN_LINK)
            if (link.isFile) {
                // 和内置音色共用模型：模型路径 = 内置的那份
                // 审查采纳：记的是“模型指纹”（以后内置模型改了文件夹名也认得出）；旧格式记的是文件夹名，也兼容
                val key = link.readText(Charsets.UTF_8).trim()
                val base = builtinLookup(key) ?: throw IllegalArgumentException("它依赖的内置模型在这个版本里没有（请重新导入完整的音色包）")
                return VoiceSpec(
                    id = dir.name, name = o.optString("name", "").trim().ifEmpty { dir.name }.take(40), engine = base.engine,
                    sid = o.optInt("sid", 0).coerceAtLeast(0),
                    gain = o.optDouble("gain", 1.0).let { if (it.isNaN() || it <= 0) 1.0 else it }.coerceIn(0.1, 5.0).toFloat(),
                    speed = o.optDouble("speed", 1.0).let { if (it.isNaN() || it <= 0) 1.0 else it }.coerceIn(0.5, 2.0).toFloat(),
                    description = o.optString("description", ""), order = o.optInt("order", 100),
                    modelInAssets = base.modelInAssets, modelDir = base.modelDir, files = base.files,
                    sample = sample, sampleInAssets = false, builtIn = false, packDir = dir, modelKey = base.modelKey,
                )
            }
            fun ex(r: String) = File(dir, r).isFile
            fun sz(r: String) = File(dir, r).length()
            return specFrom(o, dir.name, false, false, dir.absolutePath, ::ex, ::sz, sample, false, dir) { files ->
                modelKey(o.optString("engine", "vits").lowercase(), files, ::sz) { r, p, n -> readRangeFile(File(dir, r), p, n) }
            }
        }

        /** 排序：order 小的在前，其次按名字。 */
        fun sort(list: MutableList<VoiceSpec>) = list.sortWith(compareBy<VoiceSpec>({ it.order }, { it.name }))

        /** 文件夹名只用英文字母、数字、- _ .（原生库对英文路径最稳）。 */
        fun safeFolderName(preferred: String?, fallback: String?, seed: String): String {
            for (s in listOf(preferred, fallback)) {
                if (s.isNullOrBlank()) continue
                val chars = s.trim().map { if (it in 'a'..'z' || it in 'A'..'Z' || it in '0'..'9' || it == '-' || it == '_' || it == '.') it else '-' }
                var t = String(chars.toCharArray()).trim('-', '.', ' ')
                while (t.contains("--")) t = t.replace("--", "-")
                if (t.length > 40) t = t.substring(0, 40).trimEnd('-', '.')
                if (t.length >= 2 && chars.count { it != '-' } * 2 >= s.trim().length) return t
            }
            val h = MessageDigest.getInstance("SHA-256").digest(seed.toByteArray()).joinToString("") { "%02x".format(it) }
            return "voice-" + h.substring(0, 8)
        }
    }
}
