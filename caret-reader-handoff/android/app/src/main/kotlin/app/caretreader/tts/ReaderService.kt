package app.caretreader.tts

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import android.util.Log

/**
 * 前台服务：朗读时保持进程不被系统杀掉（锁屏/切到别的 App 也能继续读），
 * 通知栏里有「暂停/继续」「停止」两个按钮（对应 Windows 版的两个快捷键）。
 */
class ReaderService : Service() {
    private val listener: () -> Unit = { update() }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        Reader.addListener(listener)
    }

    override fun onDestroy() {
        Reader.removeListener(listener)
        super.onDestroy()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_TOGGLE -> Reader.toggle()
            ACTION_STOP -> Reader.stop()
        }
        try {
            if (Build.VERSION.SDK_INT >= 29) startForeground(ID, build(), ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK)
            else startForeground(ID, build())
        } catch (t: Throwable) {
            Log.w(TAG, "startForeground failed", t)
        }
        if (!Reader.isActive()) { stopForegroundCompat(); stopSelf() }
        return START_NOT_STICKY
    }

    private fun update() {
        if (!Reader.isActive()) { stopForegroundCompat(); stopSelf(); return }
        try { (getSystemService(NOTIFICATION_SERVICE) as NotificationManager).notify(ID, build()) } catch (_: Throwable) {}
    }

    @Suppress("DEPRECATION")
    private fun stopForegroundCompat() {
        if (Build.VERSION.SDK_INT >= 24) stopForeground(STOP_FOREGROUND_REMOVE) else stopForeground(true)
    }

    private fun build(): Notification {
        val nm = getSystemService(NOTIFICATION_SERVICE) as NotificationManager
        val b = if (Build.VERSION.SDK_INT >= 26) {
            if (nm.getNotificationChannel(CHANNEL) == null)
                nm.createNotificationChannel(NotificationChannel(CHANNEL, "朗读控制", NotificationManager.IMPORTANCE_LOW).apply {
                    setShowBadge(false); setSound(null, null)
                })
            Notification.Builder(this, CHANNEL)
        } else {
            @Suppress("DEPRECATION") Notification.Builder(this).setPriority(Notification.PRIORITY_LOW)
        }
        val piFlags = PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        fun pi(action: String, code: Int) = PendingIntent.getService(this, code, Intent(this, ReaderService::class.java).setAction(action), piFlags)
        val open = PendingIntent.getActivity(this, 3, Intent(this, SettingsActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK), piFlags)
        val paused = Reader.state == Reader.State.PAUSED
        @Suppress("DEPRECATION")
        return b.setSmallIcon(android.R.drawable.ic_lock_silent_mode_off)
            .setContentTitle(Reader.stateText())
            .setContentText(Reader.message)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setShowWhen(false)
            .setContentIntent(open)
            .addAction(Notification.Action.Builder(null, if (paused) "继续" else "暂停", pi(ACTION_TOGGLE, 1)).build())
            .addAction(Notification.Action.Builder(null, "停止", pi(ACTION_STOP, 2)).build())
            .build()
    }

    companion object {
        const val ACTION_TOGGLE = "app.caretreader.tts.TOGGLE"
        const val ACTION_STOP = "app.caretreader.tts.STOP"
        private const val CHANNEL = "reader"
        private const val ID = 7

        fun ensureRunning(ctx: Context) {
            try {
                val i = Intent(ctx, ReaderService::class.java)
                if (Build.VERSION.SDK_INT >= 26) ctx.startForegroundService(i) else ctx.startService(i)
            } catch (t: Throwable) {
                // Android 12+ 不允许后台启动前台服务的情况：照样朗读（无障碍服务会保持进程），只是没有通知栏按钮
                Log.w(TAG, "前台服务启动失败（照常朗读）", t)
            }
        }

        fun stopIfIdle(ctx: Context) {
            if (Reader.isActive()) return
            try { ctx.stopService(Intent(ctx, ReaderService::class.java)) } catch (_: Throwable) {}
        }
    }
}
