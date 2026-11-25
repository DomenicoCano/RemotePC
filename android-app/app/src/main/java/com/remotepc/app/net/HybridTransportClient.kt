package com.remotepc.app.net

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Canvas
import android.os.Build
import com.remotepc.app.crypto.CryptoSession
import com.remotepc.app.crypto.PacketType
import com.remotepc.app.data.PcProfile
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString
import okio.ByteString.Companion.toByteString
import org.json.JSONArray
import org.json.JSONObject
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

enum class TransportRoute { NONE, LOCAL_WIFI, CLOUD_WSS }

data class DecodedDesktopFrame(
    val frameId: Long,
    val screenWidth: Int,
    val screenHeight: Int,
    val cursorNormX: Float,
    val cursorNormY: Float,
    val isIBeam: Boolean,
    val bitmap: Bitmap
)

interface RemoteSessionListener {
    fun onStatusChanged(connected: Boolean, route: TransportRoute, detail: String)
    fun onFrameDecoded(frame: DecodedDesktopFrame)
    fun onStreamStats(fps: Int, kbps: Int, rttMs: Int)
    fun onImeHint(show: Boolean, caretNormY: Float)
    fun onMicAlert(active: Boolean, apps: List<String>)
    fun onMicState(on: Boolean, hasVbCable: Boolean, switched: Boolean, speakers: Boolean)
    fun onClipboardReceived(text: String)
    fun onMonitorsUpdated(count: Int, activeIndex: Int)
    fun onLanEndpointUpdated(newLanHostPort: String)
    fun onPcNotification(obj: JSONObject)
}

/**
 * Dual-path encrypted client:
 * - Direct Local Wi-Fi WebSocket (<5 ms) with automatic UDP LAN discovery fallback
 * - Cloud MQTT-over-WSS (works over 4G/5G without port forwarding)
 * - Full-frame + atomic dirty-rect VideoTile compositing
 * - Live RTT ping, FPS and bandwidth telemetry
 */
class HybridTransportClient(
    private val profile: PcProfile,
    initialHeight: Int,
    initialQuality: Int,
    initialFps: Int,
    private val listener: RemoteSessionListener
) {
    private val crypto = CryptoSession(profile.keyBase64Url)
    private val txLock = Any()

    private val lanClient = OkHttpClient.Builder()
        .connectTimeout(1200, TimeUnit.MILLISECONDS)
        .readTimeout(0, TimeUnit.MILLISECONDS)
        .pingInterval(10, TimeUnit.SECONDS)
        .build()

    private val cloudClient = OkHttpClient.Builder()
        .connectTimeout(8, TimeUnit.SECONDS)
        .readTimeout(0, TimeUnit.MILLISECONDS)
        .pingInterval(20, TimeUnit.SECONDS)
        .build()

    private val decodeExecutor = Executors.newSingleThreadExecutor()
    private val heartbeatExecutor = Executors.newSingleThreadScheduledExecutor()

    private val closed = AtomicBoolean(false)
    @Volatile private var activeRoute = TransportRoute.NONE
    @Volatile private var lanWs: WebSocket? = null
    @Volatile private var lanOpen = false
    @Volatile private var currentLanHostPort = profile.lanHostPort
    @Volatile private var cloudHelloAttempts = 0
    @Volatile private var lanRetryCounter = 0
    private var mqttClient: MqttOverWssClient? = null

    @Volatile var videoHeight: Int = initialHeight.coerceIn(360, 1440)
    @Volatile var jpegQuality: Int = initialQuality.coerceIn(15, 95)
    @Volatile var targetFps: Int = initialFps.coerceIn(1, 60)

    // Live telemetry counters
    private val statsLock = Any()
    private var framesInWindow = 0
    private var bytesInWindow = 0L
    private var statsWindowStartMs = System.currentTimeMillis()
    @Volatile private var latestRttMs = 0

    // Video chunk reassembly
    private var chunkFrameId = -1L
    private var chunkTotal = 0
    private var chunkParts: Array<ByteArray?> = emptyArray()
    private var chunkCount = 0

    // Frame + delta-tile compositor (used exclusively on decodeExecutor)
    private var canvasBitmap: Bitmap? = null
    private var canvasObj: Canvas? = null
    private var lastAppliedFrameId: Long = -1L
    private var lastKeyframeReqMs: Long = 0L

    fun start() {
        listener.onStatusChanged(false, TransportRoute.NONE, "Подключение к ${profile.name}...")
        startLanAttempt()
        startCloudWss()

        heartbeatExecutor.scheduleAtFixedRate({
            if (closed.get()) return@scheduleAtFixedRate
            if (activeRoute != TransportRoute.NONE) {
                cloudHelloAttempts = 0
                val tsBytes = ByteBuffer.allocate(8).putLong(System.currentTimeMillis()).array()
                sendEncrypted(PacketType.PING_PONG, tsBytes)

                // Periodically probe Local Wi-Fi while on Cloud WSS so returning home upgrades seamlessly
                if (activeRoute == TransportRoute.CLOUD_WSS && !lanOpen) {
                    lanRetryCounter++
                    if (lanRetryCounter >= 3) {
                        lanRetryCounter = 0
                        discoverLanAndConnect()
                    }
                } else {
                    lanRetryCounter = 0
                }
            } else {
                if (lanOpen) sendHello(TransportRoute.LOCAL_WIFI)
                if (mqttClient?.isConnected == true) {
                    cloudHelloAttempts++
                    if (cloudHelloAttempts > 3) {
                        cloudHelloAttempts = 0
                        mqttClient?.forceNextBroker()
                    } else {
                        sendHello(TransportRoute.CLOUD_WSS)
                    }
                }
            }

            emitStatsIfWindowElapsed(forceFlushIfIdle = true)
        }, 2, 3, TimeUnit.SECONDS)
    }

    fun stop() {
        if (!closed.compareAndSet(false, true)) return
        try { sendJson(JSONObject().put("t", "bye")) } catch (_: Exception) { }
        lanWs?.close(1000, "bye")
        lanWs = null
        mqttClient?.stop()
        mqttClient = null
        heartbeatExecutor.shutdownNow()
        decodeExecutor.execute {
            canvasBitmap?.recycle()
            canvasBitmap = null
            canvasObj = null
        }
        decodeExecutor.shutdown()
    }

    fun sendVideoConfig(height: Int, quality: Int, fps: Int): Boolean {
        videoHeight = height.coerceIn(360, 1440)
        jpegQuality = quality.coerceIn(15, 95)
        targetFps = fps.coerceIn(1, 60)
        return sendJson(
            JSONObject()
                .put("t", "video_config")
                .put("h", videoHeight)
                .put("q", jpegQuality)
                .put("fps", targetFps)
        )
    }

    fun sendJson(obj: JSONObject): Boolean {
        val route = activeRoute
        if (route == TransportRoute.NONE) return false
        return synchronized(txLock) {
            sendRawOnRoute(crypto.encryptJson(obj.toString()), route)
        }
    }

    fun sendAudioMic(adpcmPayload: ByteArray): Boolean {
        val route = activeRoute
        if (route == TransportRoute.NONE) return false
        return synchronized(txLock) {
            sendRawOnRoute(crypto.encrypt(PacketType.AUDIO_MIC, adpcmPayload), route)
        }
    }

    private fun sendEncrypted(type: Byte, payload: ByteArray): Boolean {
        val route = activeRoute
        if (route == TransportRoute.NONE) return false
        return synchronized(txLock) {
            sendRawOnRoute(crypto.encrypt(type, payload), route)
        }
    }

    private fun sendHello(route: TransportRoute) {
        val obj = JSONObject()
            .put("t", "hello")
            .put("dev", "${Build.MANUFACTURER} ${Build.MODEL}")
            .put("vh", videoHeight)
            .put("vq", jpegQuality)
            .put("vfps", targetFps)
        synchronized(txLock) {
            sendRawOnRoute(crypto.encryptJson(obj.toString()), route)
        }
    }

    private fun sendRawOnRoute(encrypted: ByteArray, route: TransportRoute): Boolean {
        return when (route) {
            TransportRoute.LOCAL_WIFI -> lanWs?.send(encrypted.toByteString()) == true
            TransportRoute.CLOUD_WSS -> mqttClient?.publish(encrypted) == true
            TransportRoute.NONE -> false
        }
    }

    private fun discoverLanAndConnect() {
        try {
            DatagramSocket().use { sock ->
                sock.broadcast = true
                sock.soTimeout = 550
                val probe = "REMOTEPC_DISCOVER".toByteArray(Charsets.UTF_8)
                val bcast = InetAddress.getByName("255.255.255.255")
                sock.send(DatagramPacket(probe, probe.size, bcast, 8788))
                val buf = ByteArray(1024)
                val respPkt = DatagramPacket(buf, buf.size)
                sock.receive(respPkt)
                val json = JSONObject(String(respPkt.data, 0, respPkt.length, Charsets.UTF_8))
                if (json.optString("id") == profile.channelId) {
                    val ip = json.optString("ip", "")
                    val port = json.optInt("port", 8787)
                    if (ip.isNotBlank()) {
                        val discovered = "$ip:$port"
                        if (discovered != currentLanHostPort) {
                            currentLanHostPort = discovered
                            listener.onLanEndpointUpdated(discovered)
                        }
                    }
                }
            }
        } catch (_: Exception) { }
        startLanAttempt()
    }

    @Synchronized
    private fun startLanAttempt() {
        if (closed.get()) return
        val hostPort = currentLanHostPort
        val host = hostPort.substringBefore(":")
        val port = hostPort.substringAfter(":", "8787").toIntOrNull() ?: 8787
        if (host.isBlank()) return

        lanWs?.cancel()
        lanOpen = false

        val url = "ws://$host:$port/ws"
        val req = Request.Builder().url(url).build()
        lanWs = lanClient.newWebSocket(req, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                if (closed.get() || lanWs !== webSocket) {
                    webSocket.close(1000, "closed")
                    return
                }
                lanOpen = true
                sendHello(TransportRoute.LOCAL_WIFI)
            }

            override fun onMessage(webSocket: WebSocket, bytes: ByteString) {
                if (lanWs !== webSocket) return
                handleEncryptedPacket(bytes.toByteArray(), TransportRoute.LOCAL_WIFI)
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                if (lanWs !== webSocket) return
                lanOpen = false
                if (activeRoute == TransportRoute.LOCAL_WIFI) {
                    activeRoute = TransportRoute.NONE
                    if (mqttClient?.isConnected == true) {
                        sendHello(TransportRoute.CLOUD_WSS)
                    } else {
                        listener.onStatusChanged(false, TransportRoute.NONE, "Переподключение...")
                    }
                }
            }

            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                if (lanWs !== webSocket) return
                lanOpen = false
            }
        })
    }

    private fun startCloudWss() {
        val cid = "rpcand_${profile.channelId}_${(System.currentTimeMillis() and 0xFFFF).toString(16)}"
        val c2h = "rpc/v1/${profile.channelId}/c2h"
        val h2c = "rpc/v1/${profile.channelId}/h2c"

        mqttClient = MqttOverWssClient(
            okHttp = cloudClient,
            clientId = cid,
            subscribeTopic = h2c,
            publishTopic = c2h,
            onConnected = {
                if (activeRoute == TransportRoute.NONE) {
                    listener.onStatusChanged(false, TransportRoute.NONE, "WSS готов, поиск ПК...")
                    sendHello(TransportRoute.CLOUD_WSS)
                }
            },
            onDisconnected = {
                if (activeRoute == TransportRoute.CLOUD_WSS) {
                    activeRoute = TransportRoute.NONE
                    listener.onStatusChanged(false, TransportRoute.NONE, "Облачный WSS переподключается...")
                }
            },
            onMessage = { bytes ->
                handleEncryptedPacket(bytes, TransportRoute.CLOUD_WSS)
            }
        )
        mqttClient?.start()
    }

    private fun handleEncryptedPacket(raw: ByteArray, source: TransportRoute) {
        if (closed.get()) return
        val pkt = crypto.decrypt(raw) ?: return

        if (activeRoute == TransportRoute.NONE || (source == TransportRoute.LOCAL_WIFI && activeRoute != TransportRoute.LOCAL_WIFI)) {
            activeRoute = source
        } else if (source != activeRoute && pkt.type != PacketType.CONTROL_JSON) {
            return
        }

        when (pkt.type) {
            PacketType.CONTROL_JSON -> handleControlJson(pkt.payload, source)
            PacketType.VIDEO_FRAME -> decodeFullFrameAsync(pkt.payload)
            PacketType.VIDEO_CHUNK -> handleVideoChunk(pkt.payload)
            PacketType.VIDEO_TILE -> decodeTileFrameAsync(pkt.payload)
            PacketType.PING_PONG -> {
                if (pkt.payload.size == 8) {
                    val sentMs = ByteBuffer.wrap(pkt.payload).long
                    val rtt = (System.currentTimeMillis() - sentMs).toInt().coerceIn(1, 9999)
                    latestRttMs = rtt
                }
            }
        }
    }

    private fun handleControlJson(utf8: ByteArray, source: TransportRoute) {
        try {
            val obj = JSONObject(String(utf8, Charsets.UTF_8))
            when (obj.optString("t")) {
                "hello_ack" -> {
                    activeRoute = source
                    val pcName = obj.optString("pc", profile.name)
                    val newLan = obj.optString("lan", "")
                    if (newLan.isNotBlank() && newLan != currentLanHostPort) {
                        currentLanHostPort = newLan
                        listener.onLanEndpointUpdated(newLan)
                        if (source == TransportRoute.CLOUD_WSS && !lanOpen) {
                            startLanAttempt()
                        }
                    }

                    val monitors = obj.optInt("monitors", 1)
                    val monIdx = obj.optInt("mon", 0)
                    listener.onMonitorsUpdated(monitors, monIdx)

                    val label = if (source == TransportRoute.LOCAL_WIFI) {
                        "$pcName • Wi-Fi LAN"
                    } else {
                        "$pcName • 4G/5G WSS"
                    }
                    listener.onStatusChanged(true, source, label)
                    listener.onMicState(
                        on = false,
                        hasVbCable = obj.optBoolean("hasVbCable", false),
                        switched = false,
                        speakers = obj.optBoolean("outputSpeakers", false)
                    )
                }

                "monitor_state" -> {
                    val monIdx = obj.optInt("mon", 0)
                    val monitors = obj.optInt("monitors", 1)
                    listener.onMonitorsUpdated(monitors, monIdx)
                }

                "clip_data" -> {
                    val text = obj.optString("text", "")
                    listener.onClipboardReceived(text)
                }

                "ime_hint" -> {
                    val show = obj.optBoolean("show", false)
                    val caretY = obj.optDouble("caretY", 0.5).toFloat()
                    listener.onImeHint(show, caretY)
                }

                "mic_alert" -> {
                    val active = obj.optBoolean("active", false)
                    val arr: JSONArray? = obj.optJSONArray("apps")
                    val apps = ArrayList<String>()
                    if (arr != null) {
                        for (i in 0 until arr.length()) {
                            apps.add(arr.optString(i))
                        }
                    }
                    listener.onMicAlert(active, apps)
                }

                "mic_state" -> {
                    listener.onMicState(
                        on = obj.optBoolean("on", false),
                        hasVbCable = obj.optBoolean("hasVbCable", false),
                        switched = obj.optBoolean("switched", false),
                        speakers = obj.optBoolean("speakers", false)
                    )
                }

                "pc_notif" -> {
                    listener.onPcNotification(obj)
                }
            }
        } catch (_: Exception) { }
    }

    @Synchronized
    private fun handleVideoChunk(data: ByteArray) {
        if (data.size <= 8) return
        val bb = ByteBuffer.wrap(data, 0, 8).order(ByteOrder.LITTLE_ENDIAN)
        val fid = bb.int.toLong() and 0xFFFFFFFFL
        val idx = bb.short.toInt() and 0xFFFF
        val total = bb.short.toInt() and 0xFFFF
        if (total <= 0 || total > 64 || idx >= total) return

        if (fid > chunkFrameId) {
            if (chunkFrameId > 0 && chunkCount < chunkTotal) {
                sendJson(JSONObject().put("t", "ack").put("fid", chunkFrameId))
            }
            chunkFrameId = fid
            chunkTotal = total
            chunkParts = arrayOfNulls(total)
            chunkCount = 0
        } else if (fid < chunkFrameId) {
            return
        }

        if (chunkParts[idx] == null) {
            chunkParts[idx] = data.copyOfRange(8, data.size)
            chunkCount++
        }

        if (chunkCount == chunkTotal) {
            var totalSize = 0
            for (p in chunkParts) totalSize += p?.size ?: 0
            val merged = ByteArray(totalSize)
            var off = 0
            for (p in chunkParts) {
                if (p != null) {
                    System.arraycopy(p, 0, merged, off, p.size)
                    off += p.size
                }
            }
            chunkParts = emptyArray()
            chunkTotal = 0
            chunkCount = 0
            decodeFullFrameAsync(merged)
        }
    }

    private fun decodeFullFrameAsync(payload: ByteArray) {
        if (payload.size <= 17 || closed.get()) return
        decodeExecutor.execute {
            if (closed.get()) return@execute
            try {
                val bb = ByteBuffer.wrap(payload, 0, 17).order(ByteOrder.LITTLE_ENDIAN)
                val frameId = bb.int.toLong() and 0xFFFFFFFFL
                val screenW = bb.short.toInt() and 0xFFFF
                val screenH = bb.short.toInt() and 0xFFFF
                bb.short // encW
                bb.short // encH
                val cursorX = (bb.short.toInt() and 0xFFFF) / 65535f
                val cursorY = (bb.short.toInt() and 0xFFFF) / 65535f
                val flags = bb.get().toInt()
                val isIBeam = (flags and 1) != 0

                val bmp = BitmapFactory.decodeByteArray(payload, 17, payload.size - 17) ?: return@execute
                ensureCanvasSize(bmp.width, bmp.height)
                canvasObj?.drawBitmap(bmp, 0f, 0f, null)
                bmp.recycle()
                lastAppliedFrameId = frameId

                val snapshot = canvasBitmap?.copy(Bitmap.Config.ARGB_8888, false) ?: return@execute
                sendJson(JSONObject().put("t", "ack").put("fid", frameId))
                recordFrameBytes(payload.size)
                listener.onFrameDecoded(
                    DecodedDesktopFrame(frameId, screenW, screenH, cursorX, cursorY, isIBeam, snapshot)
                )
            } catch (_: Exception) { }
        }
    }

    private fun decodeTileFrameAsync(payload: ByteArray) {
        if (payload.size <= 29 || closed.get()) return
        decodeExecutor.execute {
            if (closed.get()) return@execute
            try {
                val bb = ByteBuffer.wrap(payload, 0, 29).order(ByteOrder.LITTLE_ENDIAN)
                val frameId = bb.int.toLong() and 0xFFFFFFFFL
                val baseFrameId = bb.int.toLong() and 0xFFFFFFFFL
                val screenW = bb.short.toInt() and 0xFFFF
                val screenH = bb.short.toInt() and 0xFFFF
                val encW = bb.short.toInt() and 0xFFFF
                val encH = bb.short.toInt() and 0xFFFF
                val tx = bb.short.toInt() and 0xFFFF
                val ty = bb.short.toInt() and 0xFFFF
                bb.short // tw
                bb.short // th
                val cursorX = (bb.short.toInt() and 0xFFFF) / 65535f
                val cursorY = (bb.short.toInt() and 0xFFFF) / 65535f
                val flags = bb.get().toInt()
                val isIBeam = (flags and 1) != 0

                val base = canvasBitmap
                if (base == null || base.width != encW || base.height != encH || baseFrameId != lastAppliedFrameId) {
                    val now = System.currentTimeMillis()
                    if (now - lastKeyframeReqMs > 350L) {
                        lastKeyframeReqMs = now
                        sendJson(JSONObject().put("t", "req_keyframe"))
                    }
                    return@execute
                }

                val tileBmp = BitmapFactory.decodeByteArray(payload, 29, payload.size - 29) ?: return@execute
                canvasObj?.drawBitmap(tileBmp, tx.toFloat(), ty.toFloat(), null)
                tileBmp.recycle()
                lastAppliedFrameId = frameId

                val snapshot = base.copy(Bitmap.Config.ARGB_8888, false) ?: return@execute
                sendJson(JSONObject().put("t", "ack").put("fid", frameId))
                recordFrameBytes(payload.size)
                listener.onFrameDecoded(
                    DecodedDesktopFrame(frameId, screenW, screenH, cursorX, cursorY, isIBeam, snapshot)
                )
            } catch (_: Exception) { }
        }
    }

    private fun ensureCanvasSize(w: Int, h: Int) {
        val existing = canvasBitmap
        if (existing == null || existing.width != w || existing.height != h) {
            existing?.recycle()
            val created = Bitmap.createBitmap(w, h, Bitmap.Config.ARGB_8888)
            canvasBitmap = created
            canvasObj = Canvas(created)
        }
    }

    private fun recordFrameBytes(byteCount: Int) {
        synchronized(statsLock) {
            framesInWindow++
            bytesInWindow += byteCount.toLong()
        }
        emitStatsIfWindowElapsed(forceFlushIfIdle = false)
    }

    private fun emitStatsIfWindowElapsed(forceFlushIfIdle: Boolean) {
        var emit: Triple<Int, Int, Int>? = null
        val now = System.currentTimeMillis()
        synchronized(statsLock) {
            val elapsed = now - statsWindowStartMs
            if (elapsed >= 1000L && (framesInWindow > 0 || forceFlushIfIdle)) {
                val fps = ((framesInWindow * 1000f) / elapsed).toInt()
                val kbps = (((bytesInWindow / 1024.0) * 1000.0) / elapsed).toInt()
                framesInWindow = 0
                bytesInWindow = 0L
                statsWindowStartMs = now
                emit = Triple(fps, kbps, latestRttMs)
            }
        }
        emit?.let { listener.onStreamStats(it.first, it.second, it.third) }
    }
}
