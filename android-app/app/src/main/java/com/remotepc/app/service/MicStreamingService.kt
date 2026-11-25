package com.remotepc.app.service

import android.Manifest
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import android.media.audiofx.AcousticEchoCanceler
import android.media.audiofx.NoiseSuppressor
import android.os.Build
import android.os.IBinder
import androidx.core.app.NotificationCompat
import androidx.core.content.ContextCompat
import com.remotepc.app.R
import com.remotepc.app.RemoteSessionActivity
import com.remotepc.app.audio.ImaAdpcmEncoder
import com.remotepc.app.net.HybridTransportClient
import org.json.JSONObject
import java.util.concurrent.atomic.AtomicBoolean

object ActiveSessionBridge {
    @Volatile var transport: HybridTransportClient? = null
    @Volatile var isMicStreaming: Boolean = false
    @Volatile var onMicLevel: ((Float) -> Unit)? = null
    @Volatile var onMicRunningChanged: ((Boolean) -> Unit)? = null
}

/**
 * Foreground microphone service (Android 14/15/16 compliant) that captures 16kHz mono voice
 * with hardware AEC + Noise Suppressor, compresses to IMA-ADPCM (~64 kbps), and streams
 * over the active encrypted channel even when the screen turns off.
 */
class MicStreamingService : Service() {
    private val running = AtomicBoolean(false)
    private var workerThread: Thread? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            stopStreaming()
            stopSelf()
            return START_NOT_STICKY
        }

        if (ContextCompat.checkSelfPermission(this, Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            stopSelf()
            return START_NOT_STICKY
        }

        val speakers = intent?.getBooleanExtra(EXTRA_SPEAKERS, false) ?: false
        startForegroundCompat()
        startStreaming(speakers)
        return START_NOT_STICKY
    }

    private fun startForegroundCompat() {
        ensureNotificationChannel()
        val openIntent = Intent(this, RemoteSessionActivity::class.java).apply {
            flags = Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_REORDER_TO_FRONT
        }
        val pi = PendingIntent.getActivity(
            this, 0, openIntent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
        val stopIntent = Intent(this, MicStreamingService::class.java).apply { action = ACTION_STOP }
        val stopPi = PendingIntent.getService(
            this, 1, stopIntent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )

        val notification: Notification = NotificationCompat.Builder(this, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_launcher)
            .setContentTitle("RemotePC — Микрофон активен")
            .setContentText("Голос передаётся в микрофон ПК (AES-256)")
            .setOngoing(true)
            .setContentIntent(pi)
            .addAction(0, "Выключить", stopPi)
            .build()

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            startForeground(NOTIF_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE)
        } else {
            startForeground(NOTIF_ID, notification)
        }
    }

    private fun startStreaming(speakers: Boolean) {
        if (!running.compareAndSet(false, true)) return
        ActiveSessionBridge.isMicStreaming = true
        ActiveSessionBridge.onMicRunningChanged?.invoke(true)

        ActiveSessionBridge.transport?.sendJson(
            JSONObject().put("t", "mic_ctrl").put("on", true).put("speakers", speakers)
        )

        workerThread = Thread({
            val sampleRate = 16000
            val frameSamples = 640 // 40ms
            val minBuf = AudioRecord.getMinBufferSize(
                sampleRate, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT
            ).coerceAtLeast(frameSamples * 4)

            var recorder: AudioRecord? = null
            var aec: AcousticEchoCanceler? = null
            var ns: NoiseSuppressor? = null

            try {
                recorder = AudioRecord(
                    MediaRecorder.AudioSource.VOICE_COMMUNICATION,
                    sampleRate,
                    AudioFormat.CHANNEL_IN_MONO,
                    AudioFormat.ENCODING_PCM_16BIT,
                    minBuf
                )
                if (recorder.state != AudioRecord.STATE_INITIALIZED) {
                    return@Thread
                }

                val sessionId = recorder.audioSessionId
                if (AcousticEchoCanceler.isAvailable()) {
                    aec = AcousticEchoCanceler.create(sessionId)?.apply { enabled = true }
                }
                if (NoiseSuppressor.isAvailable()) {
                    ns = NoiseSuppressor.create(sessionId)?.apply { enabled = true }
                }

                val pcm = ShortArray(frameSamples)
                val encoder = ImaAdpcmEncoder()
                recorder.startRecording()

                while (running.get()) {
                    var offset = 0
                    while (offset < frameSamples && running.get()) {
                        val read = recorder.read(pcm, offset, frameSamples - offset)
                        if (read <= 0) break
                        offset += read
                    }
                    if (offset > 0 && running.get()) {
                        val level = ImaAdpcmEncoder.computeRmsNormalized(pcm, offset)
                        ActiveSessionBridge.onMicLevel?.invoke(level)
                        val packet = encoder.encodeFrame(pcm, offset)
                        ActiveSessionBridge.transport?.sendAudioMic(packet)
                    }
                }
            } catch (_: Exception) {
            } finally {
                try { recorder?.stop() } catch (_: Exception) { }
                try { recorder?.release() } catch (_: Exception) { }
                try { aec?.release() } catch (_: Exception) { }
                try { ns?.release() } catch (_: Exception) { }
                ActiveSessionBridge.onMicLevel?.invoke(0f)
            }
        }, "RemotePC-MicStream").apply { start() }
    }

    private fun stopStreaming() {
        if (!running.compareAndSet(true, false)) return
        workerThread?.interrupt()
        workerThread = null
        ActiveSessionBridge.isMicStreaming = false
        ActiveSessionBridge.onMicLevel?.invoke(0f)
        ActiveSessionBridge.onMicRunningChanged?.invoke(false)
        ActiveSessionBridge.transport?.sendJson(
            JSONObject().put("t", "mic_ctrl").put("on", false)
        )
    }

    override fun onDestroy() {
        stopStreaming()
        super.onDestroy()
    }

    private fun ensureNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
            val ch = NotificationChannel(
                CHANNEL_ID,
                "Передача микрофона на ПК",
                NotificationManager.IMPORTANCE_LOW
            )
            nm.createNotificationChannel(ch)
        }
    }

    companion object {
        private const val CHANNEL_ID = "remotepc_mic_channel"
        private const val NOTIF_ID = 4201
        const val ACTION_START = "com.remotepc.app.MIC_START"
        const val ACTION_STOP = "com.remotepc.app.MIC_STOP"
        const val EXTRA_SPEAKERS = "speakers"

        fun start(context: Context, speakers: Boolean) {
            val i = Intent(context, MicStreamingService::class.java).apply {
                action = ACTION_START
                putExtra(EXTRA_SPEAKERS, speakers)
            }
            ContextCompat.startForegroundService(context, i)
        }

        fun stop(context: Context) {
            val i = Intent(context, MicStreamingService::class.java).apply {
                action = ACTION_STOP
            }
            context.startService(i)
        }
    }
}
