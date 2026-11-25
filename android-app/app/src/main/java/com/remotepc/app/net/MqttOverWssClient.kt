package com.remotepc.app.net

import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString
import okio.ByteString.Companion.toByteString
import java.io.ByteArrayOutputStream
import java.util.concurrent.Executors
import java.util.concurrent.ScheduledFuture
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger

/**
 * Lightweight zero-dependency MQTT 3.1.1 over OkHttp WebSocket (WSS) client for Android 8-16.
 * Supports automatic failover across EMQX (8084) and HiveMQ (8884), PINGREQ keepalive, and reconnect.
 */
class MqttOverWssClient(
    private val okHttp: OkHttpClient,
    private val clientId: String,
    private val subscribeTopic: String,
    private val publishTopic: String,
    private val brokers: List<String> = DEFAULT_BROKERS,
    private val onConnected: (String) -> Unit,
    private val onDisconnected: () -> Unit,
    private val onMessage: (ByteArray) -> Unit
) {
    private val scheduler = Executors.newSingleThreadScheduledExecutor()
    private val running = AtomicBoolean(false)
    private val connected = AtomicBoolean(false)
    private val brokerIdx = AtomicInteger(0)
    private val nextPacketId = AtomicInteger(1)

    @Volatile private var ws: WebSocket? = null
    @Volatile private var pingTask: ScheduledFuture<*>? = null
    @Volatile private var lastRxMs: Long = 0L
    @Volatile private var immediateReconnect = false
    private val rxBuf = ByteArrayOutputStream()

    val isConnected: Boolean get() = connected.get()

    fun start() {
        if (!running.compareAndSet(false, true)) return
        connectNext(0)
    }

    fun stop() {
        running.set(false)
        connected.set(false)
        pingTask?.cancel(false)
        pingTask = null
        ws?.close(1000, "bye")
        ws = null
        scheduler.shutdownNow()
    }

    fun publish(payload: ByteArray): Boolean {
        val socket = ws ?: return false
        if (!connected.get()) return false
        val pkt = buildPublishPacket(publishTopic, payload)
        return socket.send(pkt.toByteString())
    }

    private fun connectNext(delayMs: Long) {
        if (!running.get()) return
        scheduler.schedule({
            if (!running.get()) return@schedule
            val url = brokers[brokerIdx.getAndIncrement() % brokers.size]
            val req = Request.Builder()
                .url(url)
                .header("Sec-WebSocket-Protocol", "mqtt")
                .build()

            synchronized(rxBuf) { rxBuf.reset() }
            ws = okHttp.newWebSocket(req, object : WebSocketListener() {
                override fun onOpen(webSocket: WebSocket, response: Response) {
                    lastRxMs = System.currentTimeMillis()
                    val connectPkt = buildConnectPacket(clientId, 30)
                    webSocket.send(connectPkt.toByteString())
                }

                override fun onMessage(webSocket: WebSocket, bytes: ByteString) {
                    lastRxMs = System.currentTimeMillis()
                    handleIncomingBytes(webSocket, url, bytes.toByteArray())
                }

                override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                    android.util.Log.w("RemotePC", "WSS failure on $url: ${t.message}")
                    handleDrop(webSocket)
                }

                override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                    android.util.Log.d("RemotePC", "WSS closed on $url: $code $reason")
                    handleDrop(webSocket)
                }
            })
        }, delayMs, TimeUnit.MILLISECONDS)
    }

    private fun handleDrop(socket: WebSocket) {
        if (ws !== socket) return
        ws = null
        pingTask?.cancel(false)
        pingTask = null
        if (connected.compareAndSet(true, false)) {
            onDisconnected()
        }
        if (running.get()) {
            val fast = immediateReconnect
            immediateReconnect = false
            connectNext(if (fast) 0L else 1800L)
        }
    }

    private fun handleIncomingBytes(socket: WebSocket, url: String, chunk: ByteArray) {
        val packets = ArrayList<Pair<Byte, ByteArray>>()
        synchronized(rxBuf) {
            rxBuf.write(chunk)
            val data = rxBuf.toByteArray()
            var offset = 0
            while (offset < data.size) {
                if (data.size - offset < 2) break
                val header = data[offset]
                var idx = offset + 1
                var mult = 1
                var remLen = 0
                var okVar = false
                while (idx < data.size) {
                    val b = data[idx++].toInt() and 0xFF
                    remLen += (b and 0x7F) * mult
                    if ((b and 0x80) == 0) {
                        okVar = true
                        break
                    }
                    mult *= 128
                    if (mult > 128 * 128 * 128) {
                        rxBuf.reset()
                        return
                    }
                }
                if (!okVar) break
                val totalLen = (idx - offset) + remLen
                if (data.size - offset < totalLen) break
                packets.add(header to data.copyOfRange(idx, idx + remLen))
                offset += totalLen
            }
            rxBuf.reset()
            if (offset < data.size) {
                rxBuf.write(data, offset, data.size - offset)
            }
        }

        for ((header, body) in packets) {
            val pktType = (header.toInt() and 0xF0) ushr 4
            when (pktType) {
                2 -> { // CONNACK
                    if (body.size >= 2 && body[1].toInt() == 0) {
                        val sub = buildSubscribePacket(nextPacketId.getAndIncrement(), subscribeTopic)
                        socket.send(sub.toByteString())
                        if (connected.compareAndSet(false, true)) {
                            startPingLoop(socket)
                            onConnected(url)
                        }
                    }
                }
                3 -> { // PUBLISH
                    if (body.size >= 2) {
                        val topicLen = ((body[0].toInt() and 0xFF) shl 8) or (body[1].toInt() and 0xFF)
                        val qos = (header.toInt() shr 1) and 0x03
                        val payloadOff = 2 + topicLen + if (qos > 0) 2 else 0
                        if (payloadOff <= body.size) {
                            onMessage(body.copyOfRange(payloadOff, body.size))
                        }
                    }
                }
            }
        }
    }

    private fun startPingLoop(socket: WebSocket) {
        pingTask?.cancel(false)
        val pingBytes = byteArrayOf(0xC0.toByte(), 0x00).toByteString()
        pingTask = scheduler.scheduleAtFixedRate({
            if (running.get() && connected.get() && ws === socket) {
                if (System.currentTimeMillis() - lastRxMs > 45_000L) {
                    socket.cancel()
                    return@scheduleAtFixedRate
                }
                socket.send(pingBytes)
            }
        }, 20, 20, TimeUnit.SECONDS)
    }

    fun forceNextBroker() {
        val socket = ws ?: return
        immediateReconnect = true
        socket.cancel()
    }

    companion object {
        val DEFAULT_BROKERS = listOf(
            "wss://test.mosquitto.org:8081",
            "wss://broker.emqx.io:8084/mqtt",
            "wss://broker.hivemq.com:8884/mqtt",
            "wss://mqtt.eclipseprojects.io:443/mqtt"
        )

        private fun buildConnectPacket(clientId: String, keepAliveSec: Int): ByteArray {
            val out = ByteArrayOutputStream()
            writeStr(out, "MQTT")
            out.write(0x04) // MQTT 3.1.1
            out.write(0x02) // Clean session
            out.write((keepAliveSec shr 8) and 0xFF)
            out.write(keepAliveSec and 0xFF)
            writeStr(out, clientId)
            return wrap(0x10, out.toByteArray())
        }

        private fun buildSubscribePacket(packetId: Int, topic: String): ByteArray {
            val out = ByteArrayOutputStream()
            out.write((packetId shr 8) and 0xFF)
            out.write(packetId and 0xFF)
            writeStr(out, topic)
            out.write(0x00) // QoS 0
            return wrap(0x82, out.toByteArray())
        }

        private fun buildPublishPacket(topic: String, payload: ByteArray): ByteArray {
            val topicBytes = topic.toByteArray(Charsets.UTF_8)
            val remLen = 2 + topicBytes.size + payload.size
            val varInt = encodeVarInt(remLen)
            val pkt = ByteArray(1 + varInt.size + remLen)
            var p = 0
            pkt[p++] = 0x30
            System.arraycopy(varInt, 0, pkt, p, varInt.size)
            p += varInt.size
            pkt[p++] = ((topicBytes.size shr 8) and 0xFF).toByte()
            pkt[p++] = (topicBytes.size and 0xFF).toByte()
            System.arraycopy(topicBytes, 0, pkt, p, topicBytes.size)
            p += topicBytes.size
            System.arraycopy(payload, 0, pkt, p, payload.size)
            return pkt
        }

        private fun writeStr(out: ByteArrayOutputStream, s: String) {
            val b = s.toByteArray(Charsets.UTF_8)
            out.write((b.size shr 8) and 0xFF)
            out.write(b.size and 0xFF)
            out.write(b)
        }

        private fun wrap(fixedHeader: Int, body: ByteArray): ByteArray {
            val varInt = encodeVarInt(body.size)
            val out = ByteArray(1 + varInt.size + body.size)
            out[0] = fixedHeader.toByte()
            System.arraycopy(varInt, 0, out, 1, varInt.size)
            System.arraycopy(body, 0, out, 1 + varInt.size, body.size)
            return out
        }

        private fun encodeVarInt(len: Int): ByteArray {
            var x = len
            val tmp = ByteArray(4)
            var i = 0
            do {
                var enc = x % 128
                x /= 128
                if (x > 0) enc = enc or 0x80
                tmp[i++] = enc.toByte()
            } while (x > 0)
            return tmp.copyOf(i)
        }
    }
}
