package com.remotepc.app.crypto

import android.util.Base64
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.SecureRandom
import java.util.concurrent.atomic.AtomicInteger
import javax.crypto.Cipher
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

object PacketType {
    const val CONTROL_JSON: Byte = 0x01
    const val VIDEO_FRAME: Byte = 0x02
    const val AUDIO_MIC: Byte = 0x03
    const val PING_PONG: Byte = 0x04
    const val VIDEO_CHUNK: Byte = 0x05
    const val VIDEO_TILE: Byte = 0x06
}

data class DecryptedPacket(
    val type: Byte,
    val seq: Int,
    val payload: ByteArray
)

/**
 * End-to-end AES-256-GCM envelope matching RemotePcHost C# CryptoSession byte-for-byte.
 * Layout: [0]=0x52, [1]=type, [2..5]=uint32 LE seq, [6..17]=12-byte nonce, [18..]=ciphertext || 16-byte tag
 * AAD = bytes[0..5]
 */
class CryptoSession(keyBase64Url: String) {
    private val keySpec: SecretKeySpec
    private val rng = SecureRandom()
    private val salt: Int = rng.nextInt()
    private val txSeq = AtomicInteger(0)
    private val threadLocalCipher = ThreadLocal.withInitial { Cipher.getInstance("AES/GCM/NoPadding") }

    init {
        val keyBytes = decodeBase64Url(keyBase64Url)
        require(keyBytes.size == 32) { "AES-256 key must be 32 bytes (got ${keyBytes.size})" }
        keySpec = SecretKeySpec(keyBytes, "AES")
    }

    fun encrypt(type: Byte, plaintext: ByteArray): ByteArray {
        val seq = txSeq.incrementAndGet()
        val header = ByteArray(18)
        header[0] = MAGIC_BYTE
        header[1] = type
        ByteBuffer.wrap(header, 2, 4).order(ByteOrder.LITTLE_ENDIAN).putInt(seq)

        val nonce = ByteArray(12)
        val nbuf = ByteBuffer.wrap(nonce).order(ByteOrder.LITTLE_ENDIAN)
        nbuf.putInt(salt)
        nbuf.putInt(seq)
        nbuf.putInt(rng.nextInt())
        System.arraycopy(nonce, 0, header, 6, 12)

        val cipher = threadLocalCipher.get()!!
        cipher.init(Cipher.ENCRYPT_MODE, keySpec, GCMParameterSpec(128, nonce))
        cipher.updateAAD(header, 0, 6)
        val cipherAndTag = cipher.doFinal(plaintext)

        val out = ByteArray(18 + cipherAndTag.size)
        System.arraycopy(header, 0, out, 0, 18)
        System.arraycopy(cipherAndTag, 0, out, 18, cipherAndTag.size)
        return out
    }

    fun encryptJson(jsonUtf8: String): ByteArray =
        encrypt(PacketType.CONTROL_JSON, jsonUtf8.toByteArray(Charsets.UTF_8))

    fun decrypt(packet: ByteArray): DecryptedPacket? {
        if (packet.size < 18 + 16) return null
        if (packet[0] != MAGIC_BYTE) return null

        val type = packet[1]
        val seq = ByteBuffer.wrap(packet, 2, 4).order(ByteOrder.LITTLE_ENDIAN).int
        val nonce = packet.copyOfRange(6, 18)

        return try {
            val cipher = threadLocalCipher.get()!!
            cipher.init(Cipher.DECRYPT_MODE, keySpec, GCMParameterSpec(128, nonce))
            cipher.updateAAD(packet, 0, 6)
            val plain = cipher.doFinal(packet, 18, packet.size - 18)
            DecryptedPacket(type, seq, plain)
        } catch (_: Exception) {
            null
        }
    }

    companion object {
        private const val MAGIC_BYTE: Byte = 0x52

        fun decodeBase64Url(s: String): ByteArray {
            val flags = Base64.URL_SAFE or Base64.NO_WRAP or Base64.NO_PADDING
            return Base64.decode(s, flags)
        }
    }
}
