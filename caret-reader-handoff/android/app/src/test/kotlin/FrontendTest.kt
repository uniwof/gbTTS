import app.caretreader.tts.PolyphoneFrontend
import java.io.File

// 用法: FrontendTestKt <frontend数据目录> <输入.txt> <输出.txt> [用户词库]
fun main(a: Array<String>) {
    val dir = File(a[0])
    val t0 = System.nanoTime()
    val f = PolyphoneFrontend.load({ File(dir, it).inputStream() }, if (a.size > 3) File(a[3]).readText() else null)
    val rt = Runtime.getRuntime(); System.gc(); Thread.sleep(200); System.gc()
    System.err.println("load %.2fs heap used %d MB".format((System.nanoTime() - t0) / 1e9, (rt.totalMemory() - rt.freeMemory()) shr 20))
    val sb = StringBuilder()
    val lines = File(a[1]).readLines()
    val t1 = System.nanoTime(); var n = 0
    for (l in lines) { val r = f.g2p(l); n += r.size; sb.append(r.joinToString(" ") { it.ch + ":" + it.pinyin }).append('\n') }
    System.err.println("g2p %d hanzi in %.0f ms".format(n, (System.nanoTime() - t1) / 1e6))
    File(a[2]).writeText(sb.toString())
}
