package com.remotepc.app.audio

import kotlin.math.abs

/**
 * Encodes 16kHz mono PCM16 frames into compact IMA-ADPCM packets (~64 kbps)
 * matching RemotePcHost ImaAdpcmCodec byte-for-byte.
 */
class ImaAdpcmEncoder {
    private var predictor = 0
    private var stepIndex = 0

    fun reset() {
        predictor = 0
        stepIndex = 0
    }

    fun encodeFrame(pcm: ShortArray, sampleCount: Int): ByteArray {
        val outLen = 6 + (sampleCount + 1) / 2
        val out = ByteArray(outLen)
        out[0] = 1 // codec = IMA-ADPCM
        out[1] = (predictor and 0xFF).toByte()
        out[2] = ((predictor shr 8) and 0xFF).toByte()
        out[3] = stepIndex.toByte()
        out[4] = (sampleCount and 0xFF).toByte()
        out[5] = ((sampleCount shr 8) and 0xFF).toByte()

        var outIdx = 6
        var packed = 0

        for (i in 0 until sampleCount) {
            val sample = pcm[i].toInt()
            var step = STEP_TABLE[stepIndex]
            var diff = sample - predictor
            var nibble = 0
            if (diff < 0) {
                nibble = 8
                diff = -diff
            }

            var vpDiff = step shr 3
            if (diff >= step) {
                nibble = nibble or 4
                diff -= step
                vpDiff += step
            }
            step = step shr 1
            if (diff >= step) {
                nibble = nibble or 2
                diff -= step
                vpDiff += step
            }
            step = step shr 1
            if (diff >= step) {
                nibble = nibble or 1
                vpDiff += step
            }

            predictor = if ((nibble and 8) != 0) predictor - vpDiff else predictor + vpDiff
            if (predictor > 32767) predictor = 32767
            else if (predictor < -32768) predictor = -32768

            stepIndex = (stepIndex + INDEX_TABLE[nibble]).coerceIn(0, 88)

            if ((i and 1) == 0) {
                packed = nibble and 0x0F
                if (i == sampleCount - 1) {
                    out[outIdx++] = packed.toByte()
                }
            } else {
                packed = packed or ((nibble and 0x0F) shl 4)
                out[outIdx++] = packed.toByte()
            }
        }

        return out
    }

    companion object {
        private val INDEX_TABLE = intArrayOf(
            -1, -1, -1, -1, 2, 4, 6, 8,
            -1, -1, -1, -1, 2, 4, 6, 8
        )

        private val STEP_TABLE = intArrayOf(
            7, 8, 9, 10, 11, 12, 13, 14, 16, 17,
            19, 21, 23, 25, 28, 31, 34, 37, 41, 45,
            50, 55, 60, 66, 73, 80, 88, 97, 107, 118,
            130, 143, 157, 173, 190, 209, 230, 253, 279, 307,
            337, 371, 408, 449, 494, 544, 598, 658, 724, 796,
            876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066,
            2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358,
            5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899,
            15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767
        )

        fun computeRmsNormalized(pcm: ShortArray, count: Int): Float {
            if (count <= 0) return 0f
            var sum = 0L
            for (i in 0 until count) {
                val v = abs(pcm[i].toInt())
                sum += v
            }
            return (sum.toFloat() / (count * 7000f)).coerceIn(0f, 1f)
        }
    }
}
