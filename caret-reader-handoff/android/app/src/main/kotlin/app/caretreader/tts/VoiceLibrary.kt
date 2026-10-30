package app.caretreader.tts

import android.content.Context
import android.net.Uri
import android.os.StatFs
import android.provider.DocumentsContract
import android.util.Log
import org.json.JSONObject
import java.io.File
import java.io.FileOutputStream
import java.io.InputStream
import java.security.MessageDigest
import java.util.zip.ZipInputStream

/**
 * 音色库（Android 部分）：内置音色 + 导入的音色包，扫描、导入、删除。
 * 对应 Windows 版 VoiceLibrary.Scan + VoiceImporter。
 */
object VoiceLibrary {
    @Volatile private var cached: VoiceCatalog? = null
    private var builtinCache: List<VoiceSpec>? = null
    private var editionName = ""

    /** 导入的音色放这里：应用私有目录（导入按钮）和 Android/data/<包名>/files/voices（用数据线从电脑拷进来）。 */
    fun importDir(ctx: Context) = File(ctx.filesDir, "voices")
    fun externalDir(ctx: Context): File? = ctx.getExternalFilesDir(null)?.let { File(it, "voices") }

    fun catalog(ctx: Context): VoiceCatalog {
        cached?.let { return it }
        synchronized(this) {
            cached?.let { return it }
            return scan(ctx).also { cached = it }
        }
    }

    /** 重新扫描（打开设置页、导入/删除之后）。 */
    fun refresh(ctx: Context): VoiceCatalog = synchronized(this) { cached = null; catalog(ctx) }

    @Synchronized fun builtins(ctx: Context): List<VoiceSpec> {
        builtinCache?.let { return it }
        val am = ctx.applicationContext.assets
        val idx = JSONObject(am.open("voices/index.json").bufferedReader(Charsets.UTF_8).use { it.readText() })
        editionName = idx.optString("edition", "")
        val arr = idx.getJSONArray("voices")
        val list = ArrayList<VoiceSpec>()
        for (i in 0 until arr.length()) {
            val e = arr.getJSONObject(i)
            val id = e.getString("id")
            val modelDir = e.getString("modelDir")
            val sizes = e.getJSONObject("sizes")
            val o = VoiceCatalog.parseManifest(am.open("voices/$id/voice.json").bufferedReader(Charsets.UTF_8).use { it.readText() })
            val sample = if (e.optBoolean("hasSample", false)) "voices/$id/sample.mp3" else null
            list.add(VoiceCatalog.specFrom(o, id, true, true, modelDir, { sizes.has(it) }, { sizes.optLong(it, 0) },
                sample, true, null) { e.getString("modelKey") })
        }
        builtinCache = list
        return list
    }

    private fun builtinLookup(bi: List<VoiceSpec>): (String) -> VoiceSpec? = { k -> bi.firstOrNull { it.modelKey == k } ?: bi.firstOrNull { it.modelDir == k } }

    private fun scan(ctx: Context): VoiceCatalog {
        val problems = ArrayList<String>()
        val all = ArrayList<VoiceSpec>()
        val bi = builtins(ctx)
        all.addAll(bi)
        val lookup = builtinLookup(bi)
        for (root in listOfNotNull(importDir(ctx), externalDir(ctx))) {
            val dirs = root.listFiles { f -> f.isDirectory && !f.name.startsWith(".") && !f.name.startsWith("_") } ?: continue
            for (d0 in dirs.sortedBy { it.name.lowercase() }) {
                // 多套了一层文件夹？看下一层
                val cands = if (File(d0, VoiceCatalog.MANIFEST).isFile) listOf(d0)
                else d0.listFiles { f -> f.isDirectory && File(f, VoiceCatalog.MANIFEST).isFile }?.toList() ?: emptyList()
                if (cands.isEmpty()) { problems.add("「${d0.name}」不是音色包（里面没有 voice.json），已跳过"); continue }
                for (d in cands) {
                    try {
                        val v = VoiceCatalog.loadDiskPack(d, lookup)
                        if (all.any { it.id.equals(v.id, true) }) { problems.add("「${v.id}」重复，已跳过"); continue }
                        all.add(v)
                    } catch (t: Throwable) {
                        problems.add("「${d.name}」无法使用：${t.message}")
                    }
                }
            }
        }
        VoiceCatalog.sort(all)
        problems.forEach { Log.w(TAG, "音色库：$it") }
        return VoiceCatalog(editionName, all, problems)
    }

    // =====================================================================
    //  导入（「+」按钮）：选 .zip 音色包，或选一个文件夹
    // =====================================================================
    class ImportResult(val added: List<String>, val skipped: List<String>, val errors: List<String>)

    private const val MAX_TOTAL = 3L shl 30   // 单次最多 3 GB

    /** 导入一个 zip（可以含一个或多个音色包文件夹）。在后台线程调用。 */
    fun importZip(ctx: Context, uri: Uri, progress: (String) -> Unit): ImportResult {
        val tmp = newTemp(ctx)
        try {
            progress("正在解压…")
            var total = 0L
            var reported = 0L
            // 审查采纳：上限 = min(3 GB, 剩余空间 − 200 MB)，异常的压缩包（“压缩炸弹”）不会先把手机存储写满
            val cap = minOf(MAX_TOTAL, StatFs(tmp.absolutePath).availableBytes - (200L shl 20))
            if (cap <= 0) throw IllegalArgumentException("手机存储空间不够")
            val tmpCanon = tmp.canonicalPath + File.separator
            ctx.contentResolver.openInputStream(uri).use { raw ->
                if (raw == null) throw IllegalArgumentException("无法读取这个文件")
                ZipInputStream(raw.buffered(1 shl 16)).use { z ->
                    while (true) {
                        val e = z.nextEntry ?: break
                        val name = e.name.replace('\\', '/').trimStart('/')
                        if (name.isEmpty() || !VoiceCatalog.safeRel(name)) continue     // 防止“../”跳出目标文件夹
                        val out = File(tmp, name)
                        if (!out.canonicalPath.startsWith(tmpCanon)) continue           // 双保险（审查采纳）
                        if (e.isDirectory) { out.mkdirs(); continue }
                        out.parentFile?.mkdirs()
                        FileOutputStream(out).use { o ->
                            val buf = ByteArray(1 shl 18)
                            while (true) {
                                val n = z.read(buf); if (n <= 0) break
                                total += n
                                if (total > cap) throw IllegalArgumentException(if (cap < MAX_TOTAL) "手机存储空间不够（解压到 ${total shr 20} MB 时已满）" else "压缩包太大（超过 3 GB）")
                                o.write(buf, 0, n)
                            }
                        }
                        if (total - reported >= (20L shl 20)) { reported = total; progress("正在解压… ${total shr 20} MB") }
                    }
                }
            }
            return installFrom(ctx, tmp, progress)
        } catch (t: Throwable) {
            return ImportResult(emptyList(), emptyList(), listOf(if (t is OutOfMemoryError) "内存不足" else (t.message ?: t.javaClass.simpleName)))
        } finally {
            tmp.deleteRecursively()
        }
    }

    /** 导入一个文件夹（系统文件选择器选的“树”）。 */
    fun importTree(ctx: Context, tree: Uri, progress: (String) -> Unit): ImportResult {
        val tmp = newTemp(ctx)
        try {
            progress("正在复制…")
            val rootDoc = DocumentsContract.getTreeDocumentId(tree)
            var copied = 0L
            fun copy(docId: String, dest: File, depth: Int) {
                if (depth > 4) return
                dest.mkdirs()
                val children = DocumentsContract.buildChildDocumentsUriUsingTree(tree, docId)
                ctx.contentResolver.query(children, arrayOf(
                    DocumentsContract.Document.COLUMN_DOCUMENT_ID, DocumentsContract.Document.COLUMN_DISPLAY_NAME,
                    DocumentsContract.Document.COLUMN_MIME_TYPE), null, null, null)?.use { c ->
                    while (c.moveToNext()) {
                        val id = c.getString(0); val name = c.getString(1) ?: continue; val mime = c.getString(2) ?: ""
                        if (!VoiceCatalog.safeRel(name) || name.contains('/')) continue
                        if (mime == DocumentsContract.Document.MIME_TYPE_DIR) copy(id, File(dest, name), depth + 1)
                        else {
                            val s = ctx.contentResolver.openInputStream(DocumentsContract.buildDocumentUriUsingTree(tree, id))
                                ?: throw IllegalArgumentException("无法读取文件：$name")      // 审查采纳：读不了就报错，不静默跳过
                            s.use { FileOutputStream(File(dest, name)).use { o -> copied += s.copyTo(o, 1 shl 18) } }
                            if (copied > MAX_TOTAL) throw IllegalArgumentException("文件夹太大（超过 3 GB）")
                            progress("正在复制… ${copied shr 20} MB")
                        }
                    }
                }
            }
            copy(rootDoc, File(tmp, "picked"), 0)
            return installFrom(ctx, tmp, progress)
        } catch (t: Throwable) {
            return ImportResult(emptyList(), emptyList(), listOf(t.message ?: t.javaClass.simpleName))
        } finally {
            tmp.deleteRecursively()
        }
    }

    private fun newTemp(ctx: Context): File {
        val root = importDir(ctx); root.mkdirs()
        root.listFiles { f -> f.name.startsWith(".import-") || f.name.startsWith(".part-") }?.forEach { it.deleteRecursively() }   // 上次没清理掉的
        return File(root, ".import-" + System.nanoTime()).also { it.mkdirs() }
    }

    /** 在临时目录里找音色包（本层或往下 3 层），校验后搬进音色库。 */
    private fun installFrom(ctx: Context, tmp: File, progress: (String) -> Unit): ImportResult {
        val packs = tmp.walkTopDown().maxDepth(4).filter { it.isFile && it.name.equals(VoiceCatalog.MANIFEST, true) }.map { it.parentFile!! }.toList()
        if (packs.isEmpty()) return ImportResult(emptyList(), emptyList(), listOf("没有找到音色包（里面应该有 voice.json）"))
        val added = ArrayList<String>(); val skipped = ArrayList<String>(); val errors = ArrayList<String>()
        val root = importDir(ctx)
        val bi = builtins(ctx)
        val lookup = builtinLookup(bi)
        // 审查采纳：整批导入只扫描一次音色库；本批新装的音色记在内存里，用来去重/找硬链接对象
        val known = ArrayList(refresh(ctx).voices)
        for (p in packs) {
            progress("正在检查 ${p.name}…")
            val spec = try { VoiceCatalog.loadDiskPack(p, lookup) } catch (t: Throwable) {
                errors.add("「${p.name}」无法使用：${t.message}"); continue
            }
            if (known.any { it.modelKey == spec.modelKey && it.sid == spec.sid }) { skipped.add("${spec.name}（已经有了）"); continue }
            val sameBuiltin = bi.firstOrNull { it.modelKey == spec.modelKey }
            val need = if (sameBuiltin != null) (256L shl 10) else p.walkTopDown().filter { it.isFile }.sumOf { it.length() }
            if (StatFs(root.absolutePath).availableBytes < need + (200L shl 20)) { errors.add("手机存储空间不够（需要 ${need shr 20} MB，另留 200 MB）"); continue }
            val o = VoiceCatalog.parseManifest(File(p, VoiceCatalog.MANIFEST).readText(Charsets.UTF_8))
            val finalDir = uniqueDir(root, VoiceCatalog.safeFolderName(o.optString("id", ""), p.name, p.absolutePath))
            // 审查采纳：先装到隐藏的临时文件夹，校验通过后再改名，中途断电/闪退不会留下半个音色包
            val dest = File(root, ".part-" + finalDir.name)
            dest.deleteRecursively()
            if (!dest.mkdirs() && !dest.isDirectory) { errors.add("「${p.name}」无法创建文件夹"); continue }
            try {
                if (sameBuiltin != null) {
                    // 和内置音色同一个模型：只留说明和试听，模型用内置的那份（记模型指纹）
                    File(p, VoiceCatalog.MANIFEST).copyTo(File(dest, VoiceCatalog.MANIFEST))
                    spec.sample?.let { sm -> File(sm).copyTo(File(dest, File(sm).name)) }
                    File(dest, VoiceCatalog.BUILTIN_LINK).writeText(sameBuiltin.modelKey)
                } else {
                    // 和已导入的音色是同一个模型：大文件用硬链接，不重复占空间（做不到就复制）
                    val twin = known.firstOrNull { !it.builtIn && !it.modelInAssets && it.modelKey == spec.modelKey }
                    p.walkTopDown().filter { it.isFile }.forEach { f ->
                        val rel = f.relativeTo(p).path
                        val out = File(dest, rel); out.parentFile?.mkdirs()
                        val tw = twin?.let { File(it.modelDir, rel) }
                        var linked = false
                        if (tw != null && tw.isFile && tw.length() == f.length() && f.length() >= (1 shl 20)) {
                            // 主模型：模型指纹（大小 + 三段哈希）已经相同；其它大文件（词典等）再完整比一次
                            val same = rel == spec.files.model || sameContent(tw, f)
                            if (same) linked = try { android.system.Os.link(tw.absolutePath, out.absolutePath); true } catch (_: Throwable) { false }
                        }
                        if (!linked && !f.renameTo(out)) f.copyTo(out, overwrite = true)
                    }
                }
                VoiceCatalog.loadDiskPack(dest, lookup)
                if (!dest.renameTo(finalDir)) throw IllegalStateException("无法改名为 ${finalDir.name}")
                known.add(VoiceCatalog.loadDiskPack(finalDir, lookup))
                added.add(spec.name)
            } catch (t: Throwable) {
                dest.deleteRecursively(); errors.add("「${p.name}」复制后校验失败：${t.message}")
            }
        }
        refresh(ctx)
        return ImportResult(added, skipped, errors)
    }

    private fun sameContent(a: File, b: File): Boolean = sha(a) == sha(b)
    private fun sha(f: File): String {
        val md = MessageDigest.getInstance("SHA-256")
        f.inputStream().use { s -> val buf = ByteArray(1 shl 16); while (true) { val n = s.read(buf); if (n <= 0) break; md.update(buf, 0, n) } }
        return md.digest().joinToString("") { "%02x".format(it) }
    }

    private fun uniqueDir(root: File, name: String): File {
        var d = File(root, name); var i = 2
        while (d.exists()) { d = File(root, "$name-$i"); i++ }
        return d
    }

    /** 删除一个导入的音色（内置的不能删）。 */
    fun delete(ctx: Context, v: VoiceSpec): Boolean {
        val d = v.packDir ?: return false
        val ok = d.deleteRecursively()
        refresh(ctx)
        return ok
    }

    fun openSample(ctx: Context, v: VoiceSpec): InputStream? = try {
        if (v.sample == null) null else if (v.sampleInAssets) ctx.assets.open(v.sample) else File(v.sample).inputStream()
    } catch (_: Throwable) { null }
}
