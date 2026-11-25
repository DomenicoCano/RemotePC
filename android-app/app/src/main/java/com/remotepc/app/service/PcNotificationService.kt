package com.remotepc.app.service

import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.os.Build
import android.os.IBinder
import androidx.core.app.NotificationCompat
import androidx.core.content.ContextCompat
import com.remotepc.app.MainActivity
import com.remotepc.app.R
import com.remotepc.app.crypto.CryptoSession
import com.remotepc.app.crypto.PacketType
import com.remotepc.app.data.PcProfile
import com.remotepc.app.data.PcProfileStore
import com.remotepc.app.net.MqttOverWssClient
import okhttp3.OkHttpClient
import org.json.JSONObject
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Lightweight always-on encrypted push bridge (specialUse foreground service).
 * Listens ONLY to dedicated notification topics (no desktop video stream), acquires a short wake-lock
 * on arrival, and displays lockscreen/heads-up alerts even when the screen is OFF.
 */
class PcNotificationService : Service() {

    private data class ProfileBridge(
        var profile: PcProfile,
        val crypto: CryptoSession,
        val mqtt: MqttOverWssClient
    )

    private val okHttp = OkHttpClient.Builder()
        .connectTimeout(8, TimeUnit.SECONDS)
        .readTimeout(0, TimeUnit.MILLISECONDS)
        .pingInterval(20, TimeUnit.SECONDS)
        .build()

    private val bridges = ConcurrentHashMap<String, ProfileBridge>()
    private val running = AtomicBoolean(true)
    private lateinit var store: PcProfileStore

    private var udpSocket: DatagramSocket? = null
    private var udpThread: Thread? = null
    private var networkCallback: ConnectivityManager.NetworkCallback? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        store = PcProfileStore(this)
        PcNotificationHelper.ensureChannels(this)
        registerNetworkCallback()
        startUdpListener()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        startForegroundCompat()

        if (intent?.action == ACTION_STOP || !store.bgNotificationsEnabled) {
            stopSelfClean()
            return START_NOT_STICKY
        }

        val profiles = store.loadAll()
        if (profiles.isEmpty()) {
            stopSelfClean()
            return START_NOT_STICKY
        }

        syncBridges(profiles)
        if (intent?.action == ACTION_SYNC) {
            triggerSyncAll()
        }
        return START_STICKY
    }

    private fun startForegroundCompat() {
        PcNotificationHelper.ensureChannels(this)
        val openIntent = Intent(this, MainActivity::class.java).apply {
            flags = Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP
        }
        val pi = PendingIntent.getActivity(
            this, 0, openIntent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )

        val notification = NotificationCompat.Builder(this, PcNotificationHelper.BRIDGE_CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_launcher)
            .setContentTitle("RemotePC — Приём уведомлений с ПК")
            .setContentText("Фоновый шифрованный канал активен (без видеопотока)")
            .setOngoing(true)
            .setSilent(true)
            .setPriority(NotificationCompat.PRIORITY_MIN)
            .setContentIntent(pi)
            .build()

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            startForeground(NOTIF_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
        } else {
            startForeground(NOTIF_ID, notification)
        }
    }

    @Synchronized
    private fun syncBridges(profiles: List<PcProfile>) {
        val wantedIds = profiles.map { it.channelId }.toSet()
        for (existingId in bridges.keys.toList()) {
            if (existingId !in wantedIds) {
                bridges.remove(existingId)?.mqtt?.stop()
            }
        }

        for (p in profiles) {
            val current = bridges[p.channelId]
            if (current != null && current.profile.keyBase64Url == p.keyBase64Url) {
                current.profile = p
                continue
            }
            current?.mqtt?.stop()

            val crypto = try {
                CryptoSession(p.keyBase64Url)
            } catch (_: Exception) {
                continue
            }

            val suffix = (System.currentTimeMillis() and 0xFFFF).toString(16)
            val cid = "rpcbg_${p.channelId}_$suffix"
            val subTopic = "rpc/v1/${p.channelId}/notify_h2c"
            val pubTopic = "rpc/v1/${p.channelId}/notify_c2h"

            val bridgeHolder = arrayOfNulls<ProfileBridge>(1)
            val mqtt = MqttOverWssClient(
                okHttp = okHttp,
                clientId = cid,
                subscribeTopic = subTopic,
                publishTopic = pubTopic,
                onConnected = {
                    val b = bridgeHolder[0]
                    if (b != null) sendSyncPacket(b)
                },
                onDisconnected = { },
                onMessage = { bytes ->
                    val b = bridgeHolder[0] ?: return@MqttOverWssClient
                    handleEncryptedPush(b, bytes)
                }
            )
            val bridge = ProfileBridge(p, crypto, mqtt)
            bridgeHolder[0] = bridge
            bridges[p.channelId] = bridge
            mqtt.start()
        }
    }

    private fun handleEncryptedPush(bridge: ProfileBridge, raw: ByteArray) {
        val pkt = bridge.crypto.decrypt(raw) ?: return
        if (pkt.type != PacketType.CONTROL_JSON) return
        try {
            val obj = JSONObject(String(pkt.payload, Charsets.UTF_8))
            when (obj.optString("t")) {
                "pc_notif" -> PcNotificationHelper.handleIncomingPcNotif(this, bridge.profile, obj)
                "notif_cursor" -> {
                    val latest = obj.optLong("latest", 0L)
                    if (latest > 0L && store.getLastNotifId(bridge.profile.channelId) <= 0L) {
                        store.setLastNotifId(bridge.profile.channelId, latest)
                    }
                }
            }
        } catch (_: Exception) { }
    }

    private fun sendSyncPacket(bridge: ProfileBridge) {
        val since = store.getLastNotifId(bridge.profile.channelId)
        val json = JSONObject().put("t", "notif_sync").put("since", since).toString()
        val enc = bridge.crypto.encryptJson(json)
        bridge.mqtt.publish(enc)
    }

    private fun sendUdpSyncIfWifi(bridge: ProfileBridge, sock: DatagramSocket) {
        val host = bridge.profile.lanHost()
        if (host.isBlank() || !isWifiConnected()) return
        try {
            val since = store.getLastNotifId(bridge.profile.channelId)
            val json = JSONObject().put("t", "notif_sync").put("since", since).toString()
            val enc = bridge.crypto.encryptJson(json)
            val pkt = ByteArray(5 + enc.size)
            pkt[0] = 'R'.code.toByte()
            pkt[1] = 'P'.code.toByte()
            pkt[2] = 'C'.code.toByte()
            pkt[3] = 'N'.code.toByte()
            pkt[4] = 'S'.code.toByte()
            System.arraycopy(enc, 0, pkt, 5, enc.size)
            val addr = InetAddress.getByName(host)
            sock.send(DatagramPacket(pkt, pkt.size, addr, 8788))
        } catch (_: Exception) { }
    }

    private fun triggerSyncAll() {
        for (b in bridges.values) {
            if (b.mqtt.isConnected) {
                sendSyncPacket(b)
            }
            val sock = udpSocket
            if (sock != null) {
                Thread { sendUdpSyncIfWifi(b, sock) }.start()
            }
        }
    }

    private fun startUdpListener() {
        udpThread = Thread({
            val sock = try {
                DatagramSocket(null).apply {
                    reuseAddress = true
                    bind(InetSocketAddress(8788))
                    soTimeout = 12_000
                }
            } catch (_: Exception) {
                try {
                    DatagramSocket().apply { soTimeout = 12_000 }
                } catch (_: Exception) {
                    null
                }
            } ?: return@Thread

            udpSocket = sock
            val buf = ByteArray(16384)
            val dp = DatagramPacket(buf, buf.size)
            var lastSyncMs = 0L

            while (running.get()) {
                try {
                    val now = System.currentTimeMillis()
                    if (now - lastSyncMs >= 12_000L) {
                        lastSyncMs = now
                        for (b in bridges.values) {
                            sendUdpSyncIfWifi(b, sock)
                        }
                    }

                    dp.length = buf.size
                    sock.receive(dp)
                    val len = dp.length
                    if (len > 5 &&
                        buf[0] == 'R'.code.toByte() &&
                        buf[1] == 'P'.code.toByte() &&
                        buf[2] == 'C'.code.toByte() &&
                        buf[3] == 'N'.code.toByte() &&
                        buf[4] == '1'.code.toByte()
                    ) {
                        val enc = buf.copyOfRange(5, len)
                        for (b in bridges.values) {
                            val before = store.getLastNotifId(b.profile.channelId)
                            handleEncryptedPush(b, enc)
                            if (store.getLastNotifId(b.profile.channelId) != before) break
                        }
                    }
                } catch (_: java.net.SocketTimeoutException) {
                    // Expected periodic wakeup for LAN UDP sync
                } catch (_: Exception) {
                    if (!running.get()) break
                    try { Thread.sleep(1000) } catch (_: Exception) { break }
                }
            }
            try { sock.close() } catch (_: Exception) { }
        }, "RemotePC-UdpNotify").apply {
            isDaemon = true
            start()
        }
    }

    private fun registerNetworkCallback() {
        try {
            val cm = getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
            val cb = object : ConnectivityManager.NetworkCallback() {
                override fun onAvailable(network: Network) {
                    triggerSyncAll()
                }
            }
            cm.registerDefaultNetworkCallback(cb)
            networkCallback = cb
        } catch (_: Exception) { }
    }

    private fun isWifiConnected(): Boolean {
        return try {
            val cm = getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
            val caps = cm.getNetworkCapabilities(cm.activeNetwork)
            caps?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) == true
        } catch (_: Exception) {
            false
        }
    }

    private fun stopSelfClean() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
            stopForeground(STOP_FOREGROUND_REMOVE)
        } else {
            @Suppress("DEPRECATION")
            stopForeground(true)
        }
        stopSelf()
    }

    override fun onDestroy() {
        running.set(false)
        networkCallback?.let {
            try {
                val cm = getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
                cm.unregisterNetworkCallback(it)
            } catch (_: Exception) { }
        }
        networkCallback = null
        try { udpSocket?.close() } catch (_: Exception) { }
        udpThread?.interrupt()
        bridges.values.forEach { it.mqtt.stop() }
        bridges.clear()
        super.onDestroy()
    }

    companion object {
        private const val NOTIF_ID = 4202
        const val ACTION_SYNC = "com.remotepc.app.NOTIF_SYNC"
        const val ACTION_STOP = "com.remotepc.app.NOTIF_STOP"

        fun syncState(context: Context, requestImmediateSync: Boolean = false) {
            val appCtx = context.applicationContext
            val store = PcProfileStore(appCtx)
            val profiles = store.loadAll()
            if (store.bgNotificationsEnabled && profiles.isNotEmpty()) {
                val i = Intent(appCtx, PcNotificationService::class.java).apply {
                    if (requestImmediateSync) action = ACTION_SYNC
                }
                try {
                    ContextCompat.startForegroundService(appCtx, i)
                } catch (_: Exception) { }
            } else {
                try {
                    appCtx.stopService(Intent(appCtx, PcNotificationService::class.java))
                } catch (_: Exception) { }
            }
        }
    }
}
