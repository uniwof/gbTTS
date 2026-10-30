package app.caretreader.tts

import android.app.Application
import android.content.Context
import android.util.Log
import java.io.File

/** 进程启动时：后台加载多音字前端（约 1~3 秒，不阻塞界面）。 */
class App : Application() {
    override fun onCreate() {
        super.onCreate()
        ensureFrontend(this)
    }

    companion object {
        /** 用户自定义词库（对应 Windows 版 exe 旁边的 用户自定义词库.txt）。第一次运行时从 APK 里复制一份默认的。 */
        fun userDictFile(ctx: Context): File {
            val f = File(ctx.filesDir, "user_dict.txt")
            if (!f.exists()) try {
                ctx.assets.open("frontend/user_dict_default.txt").use { s -> f.outputStream().use { s.copyTo(it) } }
            } catch (t: Throwable) { Log.w(TAG, "默认用户词库复制失败", t) }
            return f
        }

        private fun opener(ctx: Context): (String) -> java.io.InputStream {
            val am = ctx.applicationContext.assets
            return { name -> am.open("frontend/$name") }
        }

        fun ensureFrontend(ctx: Context) {
            val app = ctx.applicationContext
            PolyphoneFrontend.startLoading(opener(app), { readUserDict(app) }) { Log.i(TAG, it) }
        }

        fun reloadFrontend(ctx: Context) {
            val app = ctx.applicationContext
            Thread { PolyphoneFrontend.reload(opener(app), { readUserDict(app) }) { Log.i(TAG, it) } }.start()
        }

        private fun readUserDict(ctx: Context): String? = try { userDictFile(ctx).readText(Charsets.UTF_8) } catch (_: Throwable) { null }
    }
}
