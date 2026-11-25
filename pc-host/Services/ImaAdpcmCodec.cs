using System.Buffers.Binary;

namespace RemotePcHost.Services;

/// <summary>
/// Decodes 16kHz mono IMA-ADPCM or raw PCM16 voice packets sent from the Android client.
/// Wire format:
///   byte[0]: codec (0 = PCM16LE, 1 = IMA-ADPCM)
///   For codec == 1:
///     int16 predictor (LE)
///     byte stepIndex (0..88)
///     uint16 sampleCount (LE)
///     byte[] nibbles (low nibble first, then high nibble)
/// </summary>
public static class ImaAdpcmCodec
{
    private static readonly int[] IndexTable =
    {
        -1, -1, -1, -1, 2, 4, 6, 8,
        -1, -1, -1, -1, 2, 4, 6, 8
    };

    private static readonly int[] StepTable =
    {
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17,
        19, 21, 23, 25, 28, 31, 34, 37, 41, 45,
        50, 55, 60, 66, 73, 80, 88, 97, 107, 118,
        130, 143, 157, 173, 190, 209, 230, 253, 279, 307,
        337, 371, 408, 449, 494, 544, 598, 658, 724, 796,
        876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066,
        2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358,
        5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899,
        15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767
    };

    public static byte[] DecodePacketToPcm16(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2)
            return Array.Empty<byte>();

        byte codec = payload[0];
        if (codec == 0)
        {
            return payload.Slice(1).ToArray();
        }

        if (codec != 1 || payload.Length < 6)
            return Array.Empty<byte>();

        int predictor = BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(1, 2));
        int stepIndex = Math.Clamp((int)payload[3], 0, 88);
        int sampleCount = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(4, 2));
        ReadOnlySpan<byte> data = payload.Slice(6);

        int maxSamples = Math.Min(sampleCount, data.Length * 2);
        if (maxSamples <= 0)
            return Array.Empty<byte>();

        byte[] pcm = new byte[maxSamples * 2];
        int outPos = 0;

        for (int i = 0; i < maxSamples; i++)
        {
            byte packed = data[i >> 1];
            int nibble = (i & 1) == 0 ? (packed & 0x0F) : ((packed >> 4) & 0x0F);

            int step = StepTable[stepIndex];
            int diff = step >> 3;
            if ((nibble & 4) != 0) diff += step;
            if ((nibble & 2) != 0) diff += step >> 1;
            if ((nibble & 1) != 0) diff += step >> 2;

            if ((nibble & 8) != 0)
                predictor -= diff;
            else
                predictor += diff;

            if (predictor > 32767) predictor = 32767;
            else if (predictor < -32768) predictor = -32768;

            stepIndex = Math.Clamp(stepIndex + IndexTable[nibble], 0, 88);

            pcm[outPos++] = (byte)(predictor & 0xFF);
            pcm[outPos++] = (byte)((predictor >> 8) & 0xFF);
        }

        return pcm;
    }
}
