using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RemotePcHost.Security;

public enum PacketType : byte
{
    ControlJson = 0x01,
    VideoFrame = 0x02,
    AudioMic = 0x03,
    PingPong = 0x04,
    VideoChunk = 0x05,
    VideoTile = 0x06
}

public sealed class HostConfig
{
    public string ChannelId { get; set; } = "";
    public string KeyBase64Url { get; set; } = "";
    public int LanPort { get; set; } = 8787;
    public int DiscoveryPort { get; set; } = 8788;
    public string? BaselineMicDeviceId { get; set; }
    public string? BaselineMicDeviceName { get; set; }
    public bool OutputVoiceToSpeakers { get; set; } = false;
    public bool AutoStartWithWindows { get; set; } = false;
    public bool AutoSwitchDefaultMic { get; set; } = true;
    public bool ForwardPcNotifications { get; set; } = true;
}

public sealed class CryptoSession : IDisposable
{
    public const byte MagicByte = 0x52; // 'R'
    public const int HeaderSize = 18;   // 1 magic + 1 type + 4 seq + 12 nonce
    public const int TagSize = 16;
    private const int MaxReplaySlots = 8;

    private sealed class ClientReplaySlot
    {
        public uint Salt;
        public uint MaxSeq;
        public ulong WindowMask;
        public long LastTickMs;
    }

    private readonly byte[] _key;
    private readonly AesGcm _aes;
    private readonly uint _salt;
    private uint _txSeq;
    private readonly object _txLock = new();
    private readonly object _decryptLock = new();
    private readonly object _rxLock = new();
    private readonly List<ClientReplaySlot> _rxSlots = new(MaxReplaySlots);

    public HostConfig Config { get; }
    public static string ConfigDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RemotePcHost");
    public static string ConfigPath => Path.Combine(ConfigDir, "config.json");

    public CryptoSession(HostConfig config)
    {
        Config = config;
        _key = FromBase64Url(config.KeyBase64Url);
        if (_key.Length != 32)
            throw new InvalidOperationException("AES-256 key must be 32 bytes.");

        _aes = new AesGcm(_key, TagSize);
        Span<byte> saltBytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(saltBytes);
        _salt = BinaryPrimitives.ReadUInt32LittleEndian(saltBytes);
    }

    public static HostConfig LoadOrCreateConfig()
    {
        Directory.CreateDirectory(ConfigDir);
        HostConfig? cfg = null;
        if (File.Exists(ConfigPath))
        {
            try
            {
                string json = File.ReadAllText(ConfigPath, Encoding.UTF8);
                cfg = JsonSerializer.Deserialize<HostConfig>(json);
            }
            catch
            {
                cfg = null;
            }
        }

        bool dirty = false;
        if (cfg == null)
        {
            cfg = new HostConfig();
            dirty = true;
        }

        if (string.IsNullOrWhiteSpace(cfg.ChannelId) || cfg.ChannelId.Length < 12)
        {
            cfg.ChannelId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            dirty = true;
        }

        if (string.IsNullOrWhiteSpace(cfg.KeyBase64Url) || TryDecodeKeyLength(cfg.KeyBase64Url) != 32)
        {
            cfg.KeyBase64Url = ToBase64Url(RandomNumberGenerator.GetBytes(32));
            dirty = true;
        }

        if (dirty) SaveConfig(cfg);
        return cfg;
    }

    public static void SaveConfig(HostConfig cfg)
    {
        Directory.CreateDirectory(ConfigDir);
        var opts = new JsonSerializerOptions { WriteIndented = true };
        string json = JsonSerializer.Serialize(cfg, opts);
        string tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, json, Encoding.UTF8);
        File.Move(tmp, ConfigPath, overwrite: true);
    }

    public void ResetReplayWindow()
    {
        lock (_rxLock)
        {
            _rxSlots.Clear();
        }
    }

    public string BuildPairingUri(string lanIp)
    {
        string name = Uri.EscapeDataString(Environment.MachineName);
        string lan = Uri.EscapeDataString($"{lanIp}:{Config.LanPort}");
        return $"remotepc://pair?v=1&id={Config.ChannelId}&k={Config.KeyBase64Url}&lan={lan}&n={name}";
    }

    public byte[] Encrypt(PacketType type, ReadOnlySpan<byte> plaintext)
    {
        byte[] output = new byte[HeaderSize + plaintext.Length + TagSize];
        lock (_txLock)
        {
            uint seq = ++_txSeq;
            output[0] = MagicByte;
            output[1] = (byte)type;
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(2, 4), seq);

            Span<byte> nonce = output.AsSpan(6, 12);
            BinaryPrimitives.WriteUInt32LittleEndian(nonce.Slice(0, 4), _salt);
            BinaryPrimitives.WriteUInt32LittleEndian(nonce.Slice(4, 4), seq);
            RandomNumberGenerator.Fill(nonce.Slice(8, 4));

            ReadOnlySpan<byte> aad = output.AsSpan(0, 6);
            Span<byte> ciphertext = output.AsSpan(HeaderSize, plaintext.Length);
            Span<byte> tag = output.AsSpan(HeaderSize + plaintext.Length, TagSize);

            _aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
        }
        return output;
    }

    public byte[] EncryptJson(object payload)
    {
        byte[] utf8 = JsonSerializer.SerializeToUtf8Bytes(payload);
        return Encrypt(PacketType.ControlJson, utf8);
    }

    public bool TryDecrypt(ReadOnlySpan<byte> packet, out PacketType type, out uint seq, out byte[] plaintext)
    {
        type = default;
        seq = 0;
        plaintext = Array.Empty<byte>();

        if (packet.Length < HeaderSize + TagSize)
            return false;
        if (packet[0] != MagicByte)
            return false;

        type = (PacketType)packet[1];
        seq = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(2, 4));
        ReadOnlySpan<byte> nonce = packet.Slice(6, 12);
        ReadOnlySpan<byte> aad = packet.Slice(0, 6);

        int cipherLen = packet.Length - HeaderSize - TagSize;
        ReadOnlySpan<byte> ciphertext = packet.Slice(HeaderSize, cipherLen);
        ReadOnlySpan<byte> tag = packet.Slice(HeaderSize + cipherLen, TagSize);

        byte[] decrypted = new byte[cipherLen];
        try
        {
            lock (_decryptLock)
            {
                _aes.Decrypt(nonce, ciphertext, tag, decrypted, aad);
            }
        }
        catch (CryptographicException)
        {
            return false;
        }

        uint clientSalt = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(0, 4));

        // Anti-replay sliding window (64 packets per independent client salt, up to 8 concurrent sessions)
        lock (_rxLock)
        {
            long nowMs = Environment.TickCount64;
            ClientReplaySlot? slot = null;
            int oldestIdx = 0;
            for (int i = 0; i < _rxSlots.Count; i++)
            {
                if (_rxSlots[i].Salt == clientSalt)
                {
                    slot = _rxSlots[i];
                    break;
                }
                if (_rxSlots[i].LastTickMs < _rxSlots[oldestIdx].LastTickMs)
                    oldestIdx = i;
            }

            if (slot == null)
            {
                slot = new ClientReplaySlot
                {
                    Salt = clientSalt,
                    MaxSeq = seq,
                    WindowMask = 1UL,
                    LastTickMs = nowMs
                };
                if (_rxSlots.Count < MaxReplaySlots)
                    _rxSlots.Add(slot);
                else
                    _rxSlots[oldestIdx] = slot;
            }
            else if (seq > slot.MaxSeq)
            {
                uint diff = seq - slot.MaxSeq;
                slot.WindowMask = diff >= 64 ? 1UL : ((slot.WindowMask << (int)diff) | 1UL);
                slot.MaxSeq = seq;
                slot.LastTickMs = nowMs;
            }
            else
            {
                uint diff = slot.MaxSeq - seq;
                if (diff >= 64)
                    return false; // Too old / replayed
                ulong bit = 1UL << (int)diff;
                if ((slot.WindowMask & bit) != 0)
                    return false; // Duplicate / replayed
                slot.WindowMask |= bit;
                slot.LastTickMs = nowMs;
            }
        }

        plaintext = decrypted;
        return true;
    }

    public static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static byte[] FromBase64Url(string s)
    {
        string padded = s.Replace('-', '+').Replace('_', '/');
        int mod = padded.Length % 4;
        if (mod > 0) padded += new string('=', 4 - mod);
        return Convert.FromBase64String(padded);
    }

    private static int TryDecodeKeyLength(string s)
    {
        try { return FromBase64Url(s).Length; }
        catch { return -1; }
    }

    public void Dispose()
    {
        _aes.Dispose();
        Array.Clear(_key);
    }
}
