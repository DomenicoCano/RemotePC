package com.remotepc.app.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.PowerManager
import androidx.core.app.NotificationCompat
import com.remotepc.app.R
import com.remotepc.app.RemoteSessionActivity
import com.remotepc.app.data.PcNotificationRecord
import com.remotepc.app.data.PcProfile
import com.remotepc.app.data.PcProfileStore
import org.json.JSONObject

object PcNotificationHelper {
    const val ALERT_CHANNEL_ID = "remotepc_pc_alerts_v1"
    const val BRIDGE_CHANNEL_ID = "remotepc_bg_bridge_v1"
    private const val MAX_DEDUP_SIZE = 256

    private val seenKeys = LinkedHashSet<String>()
    private val mainHandler = Handler(Looper.getMainLooper())
    @Volatile var onHistoryUpdated: (() -> Unit)? = null

    fun ensureChannels(context: Context) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        val nm = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager

        val alertCh = NotificationChannel(
            ALERT_CHANNEL_ID,
            "Уведомления с Windows ПК",
            NotificationManager.IMPORTANCE_HIGH
        ).apply {
            description = "Мгновенная доставка уведомлений с ПК даже при выключенном экране"
            lockscreenVisibility = Notification.VISIBILITY_PUBLIC
            enableVibration(true)
            enableLights(true)
            setShowBadge(true)
        }
        nm.createNotificationChannel(alertCh)

        val bridgeCh = NotificationChannel(
            BRIDGE_CHANNEL_ID,
            "Фоновая служба уведомлений ПК",
            NotificationManager.IMPORTANCE_MIN
        ).apply {
            description = "Тихий фоновый канал приёма зашифрованных уведомлений с ПК"
            setShowBadge(false)
            enableVibration(false)
            setSound(null, null)
        }
        nm.createNotificationChannel(bridgeCh)
    }

    fun handleIncomingPcNotif(context: Context, profile: PcProfile, obj: JSONObject): Boolean {
        val rawId = obj.optLong("id", 0L)
        val tsMs = obj.optLong("ts", System.currentTimeMillis()).takeIf { it > 0L } ?: System.currentTimeMillis()
        val id = if (rawId > 0L) rawId else tsMs

        val app = obj.optString("app", "Windows").trim().ifBlank { "Windows" }
        val title = obj.optString("title", "").trim().ifBlank { app }
        val body = obj.optString("body", "").trim()
        val dedupKey = "${profile.channelId}:$id"

        synchronized(seenKeys) {
            if (!seenKeys.add(dedupKey)) return false
            while (seenKeys.size > MAX_DEDUP_SIZE) {
                val it = seenKeys.iterator()
                if (it.hasNext()) {
                    it.next()
                    it.remove()
                }
            }
        }

        val store = PcProfileStore(context)
        store.setLastNotifId(profile.channelId, id)
        store.appendNotificationHistory(
            PcNotificationRecord(
                channelId = profile.channelId,
                pcName = profile.name,
                id = id,
                app = app,
                title = title,
                body = body,
                tsMs = tsMs
            )
        )

        postLockscreenAlert(context, profile, id, app, title, body, tsMs)
        mainHandler.post { onHistoryUpdated?.invoke() }
        return true
    }

    private fun postLockscreenAlert(
        context: Context,
        profile: PcProfile,
        id: Long,
        app: String,
        title: String,
        body: String,
        tsMs: Long
    ) {
        ensureChannels(context)

        try {
            val pm = context.getSystemService(Context.POWER_SERVICE) as? PowerManager
            val wl = pm?.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "RemotePC:NotifWake")
            wl?.setReferenceCounted(false)
            wl?.acquire(3000L)
        } catch (_: Exception) { }

        val androidNotifId = (((profile.channelId.hashCode() * 31) xor id.toInt()) and 0x7FFFFFFF)
            .let { if (it < 10000) it + 10000 else it }

        val openIntent = Intent(context, RemoteSessionActivity::class.java).apply {
            flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP
            putExtra(RemoteSessionActivity.EXTRA_PROFILE_JSON, profile.toJson().toString())
        }
        val openPi = PendingIntent.getActivity(
            context,
            androidNotifId,
            openIntent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )

        val displayTitle = if (title.equals(app, ignoreCase = true)) app else "$app • $title"
        val displayBody = body.ifBlank { "Новое уведомление на ${profile.name}" }

        val notif = NotificationCompat.Builder(context, ALERT_CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_launcher)
            .setContentTitle(displayTitle)
            .setContentText(displayBody)
            .setSubText(profile.name)
            .setStyle(NotificationCompat.BigTextStyle().bigText(displayBody).setBigContentTitle(displayTitle))
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setCategory(NotificationCompat.CATEGORY_MESSAGE)
            .setVisibility(NotificationCompat.VISIBILITY_PUBLIC)
            .setDefaults(NotificationCompat.DEFAULT_ALL)
            .setWhen(tsMs)
            .setShowWhen(true)
            .setAutoCancel(true)
            .setContentIntent(openPi)
            .addAction(0, "Открыть ПК", openPi)
            .build()

        try {
            val nm = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
            nm.notify(androidNotifId, notif)
        } catch (_: SecurityException) { }
    }
}
