using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RemotePcHost.Services;

/// <summary>
/// Pure TcpListener-based RFC 6455 WebSocket server + UDP LAN discovery responder.
/// Requires ZERO administrator privileges or netsh URLACL reservations on Windows.
/// </summary>
public sealed class LocalWebSocketServer : IDisposable
{
    private const string WsMagicGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly int _wsPort;
    private readonly int _udpPort;
    private readonly Func<object> _discoveryInfoProvider;

    private TcpListener? _listener;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;

    private readonly object _clientLock = new();
    private TcpClient? _activeClient;
    private NetworkStream? _activeStream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public bool HasClient => _activeClient?.Connected == true;

    public event Action<byte[]>? MessageReceived;
    public event Action<bool, string>? ClientStateChanged;
    public event Action<byte[], IPEndPoint>? UdpNotifySyncReceived;

    public LocalWebSocketServer(int wsPort, int udpPort, Func<object> discoveryInfoProvider)
    {
        _wsPort = wsPort;
        _udpPort = udpPort;
        _discoveryInfoProvider = discoveryInfoProvider;
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();

        _listener = new TcpListener(IPAddress.Any, _wsPort);
        _listener.Start();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));

        try
        {
            _udp = new UdpClient(_udpPort);
            _udp.EnableBroadcast = true;
            _ = Task.Run(() => UdpDiscoveryLoopAsync(_cts.Token));
        }
        catch
        {
            // UDP discovery is optional if port is busy
        }
    }

    public async Task<bool> SendBinaryAsync(byte[] payload, CancellationToken ct = default)
    {
        NetworkStream? stream;
        lock (_clientLock)
        {
            stream = _activeStream;
        }
        if (stream == null) return false;

        byte[] frame = EncodeServerFrame(0x02, payload);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(frame, ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            DropActiveClient();
            return false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            try
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                client.NoDelay = true;
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch { await Task.Delay(200, ct).ConfigureAwait(false); }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        string remoteIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "LAN";
        NetworkStream stream = client.GetStream();

        try
        {
            if (!await PerformWebSocketHandshakeAsync(stream, ct).ConfigureAwait(false))
            {
                client.Dispose();
                return;
            }

            DropActiveClient();
            lock (_clientLock)
            {
                _activeClient = client;
                _activeStream = stream;
            }
            ClientStateChanged?.Invoke(true, remoteIp);

            using var msgAccumulator = new MemoryStream();
            while (!ct.IsCancellationRequested && client.Connected)
            {
                var (ok, fin, opcode, payload) = await ReadFrameAsync(stream, ct).ConfigureAwait(false);
                if (!ok) break;

                if (opcode == 0x08) // Close
                {
                    break;
                }
                else if (opcode == 0x09) // Ping -> Pong
                {
                    byte[] pong = EncodeServerFrame(0x0A, payload);
                    await _writeLock.WaitAsync(ct).ConfigureAwait(false);
                    try { await stream.WriteAsync(pong, ct).ConfigureAwait(false); }
                    finally { _writeLock.Release(); }
                    continue;
                }
                else if (opcode == 0x0A) // Pong
                {
                    continue;
                }

                if (opcode == 0x01 || opcode == 0x02)
                    msgAccumulator.SetLength(0);

                if (payload.Length > 0)
                    msgAccumulator.Write(payload, 0, payload.Length);

                if (fin && msgAccumulator.Length > 0)
                {
                    byte[] msg = msgAccumulator.ToArray();
                    msgAccumulator.SetLength(0);
                    MessageReceived?.Invoke(msg);
                }
            }
        }
        catch
        {
            // Disconnected
        }
        finally
        {
            bool wasActive = false;
            lock (_clientLock)
            {
                if (_activeClient == client)
                {
                    _activeClient = null;
                    _activeStream = null;
                    wasActive = true;
                }
            }
            try { client.Dispose(); } catch { }
            if (wasActive)
                ClientStateChanged?.Invoke(false, "");
        }
    }

    private void DropActiveClient()
    {
        TcpClient? old = null;
        lock (_clientLock)
        {
            old = _activeClient;
            _activeClient = null;
            _activeStream = null;
        }
        if (old != null)
        {
            try { old.Dispose(); } catch { }
            ClientStateChanged?.Invoke(false, "");
        }
    }

    private static async Task<bool> PerformWebSocketHandshakeAsync(NetworkStream stream, CancellationToken ct)
    {
        using var headerMs = new MemoryStream();
        byte[] buf = new byte[1];
        int matchState = 0;
        while (headerMs.Length < 8192)
        {
            int n = await stream.ReadAsync(buf, ct).ConfigureAwait(false);
            if (n <= 0) return false;
            byte b = buf[0];
            headerMs.WriteByte(b);
            matchState = (matchState, b) switch
            {
                (0, (byte)'\r') => 1,
                (1, (byte)'\n') => 2,
                (2, (byte)'\r') => 3,
                (3, (byte)'\n') => 4,
                (_, (byte)'\r') => 1,
                _ => 0
            };
            if (matchState == 4) break;
        }

        string request = Encoding.ASCII.GetString(headerMs.ToArray());
        string? wsKey = null;
        foreach (string line in request.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
            {
                wsKey = line.Substring("Sec-WebSocket-Key:".Length).Trim();
                break;
            }
        }

        if (string.IsNullOrEmpty(wsKey))
        {
            byte[] resp400 = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 11\r\n\r\nRemotePC OK");
            await stream.WriteAsync(resp400, ct).ConfigureAwait(false);
            return false;
        }

        string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(wsKey + WsMagicGuid)));
        string response =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n";

        byte[] respBytes = Encoding.ASCII.GetBytes(response);
        await stream.WriteAsync(respBytes, ct).ConfigureAwait(false);
        return true;
    }

    private static async Task<(bool Ok, bool Fin, byte Opcode, byte[] Payload)> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        byte[] hdr = new byte[2];
        if (!await ReadExactAsync(stream, hdr, ct).ConfigureAwait(false))
            return (false, false, 0, Array.Empty<byte>());

        bool fin = (hdr[0] & 0x80) != 0;
        byte opcode = (byte)(hdr[0] & 0x0F);
        bool masked = (hdr[1] & 0x80) != 0;
        ulong len = (ulong)(hdr[1] & 0x7F);

        if (len == 126)
        {
            byte[] ext = new byte[2];
            if (!await ReadExactAsync(stream, ext, ct).ConfigureAwait(false))
                return (false, false, 0, Array.Empty<byte>());
            len = BinaryPrimitives.ReadUInt16BigEndian(ext);
        }
        else if (len == 127)
        {
            byte[] ext = new byte[8];
            if (!await ReadExactAsync(stream, ext, ct).ConfigureAwait(false))
                return (false, false, 0, Array.Empty<byte>());
            len = BinaryPrimitives.ReadUInt64BigEndian(ext);
        }

        if (len > 4 * 1024 * 1024)
            return (false, false, 0, Array.Empty<byte>());

        byte[] mask = new byte[4];
        if (masked && !await ReadExactAsync(stream, mask, ct).ConfigureAwait(false))
            return (false, false, 0, Array.Empty<byte>());

        byte[] payload = new byte[(int)len];
        if (len > 0 && !await ReadExactAsync(stream, payload, ct).ConfigureAwait(false))
            return (false, false, 0, Array.Empty<byte>());

        if (masked)
        {
            for (int i = 0; i < payload.Length; i++)
                payload[i] ^= mask[i & 3];
        }

        return (true, fin, opcode, payload);
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct).ConfigureAwait(false);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    private static byte[] EncodeServerFrame(byte opcode, byte[] payload)
    {
        int len = payload.Length;
        int headerLen = len < 126 ? 2 : (len <= 65535 ? 4 : 10);
        byte[] frame = new byte[headerLen + len];
        frame[0] = (byte)(0x80 | opcode);

        if (len < 126)
        {
            frame[1] = (byte)len;
        }
        else if (len <= 65535)
        {
            frame[1] = 126;
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2, 2), (ushort)len);
        }
        else
        {
            frame[1] = 127;
            BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(2, 8), (ulong)len);
        }

        if (len > 0)
            Buffer.BlockCopy(payload, 0, frame, headerLen, len);
        return frame;
    }

    public async Task<bool> SendUdpNotificationAsync(byte[] encryptedPacket, IPEndPoint? target = null)
    {
        var udp = _udp;
        if (udp == null) return false;
        try
        {
            byte[] datagram = new byte[5 + encryptedPacket.Length];
            datagram[0] = (byte)'R';
            datagram[1] = (byte)'P';
            datagram[2] = (byte)'C';
            datagram[3] = (byte)'N';
            datagram[4] = (byte)'1';
            Buffer.BlockCopy(encryptedPacket, 0, datagram, 5, encryptedPacket.Length);
            IPEndPoint dest = target ?? new IPEndPoint(IPAddress.Broadcast, _udpPort);
            await udp.SendAsync(datagram, datagram.Length, dest).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task UdpDiscoveryLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _udp != null)
        {
            try
            {
                var res = await _udp.ReceiveAsync(ct).ConfigureAwait(false);
                if (res.Buffer.Length > 5 &&
                    res.Buffer[0] == (byte)'R' &&
                    res.Buffer[1] == (byte)'P' &&
                    res.Buffer[2] == (byte)'C' &&
                    res.Buffer[3] == (byte)'N' &&
                    res.Buffer[4] == (byte)'S')
                {
                    byte[] payload = new byte[res.Buffer.Length - 5];
                    Buffer.BlockCopy(res.Buffer, 5, payload, 0, payload.Length);
                    UdpNotifySyncReceived?.Invoke(payload, res.RemoteEndPoint);
                    continue;
                }

                string req = Encoding.UTF8.GetString(res.Buffer);
                if (req.StartsWith("REMOTEPC_DISCOVER", StringComparison.Ordinal))
                {
                    byte[] resp = JsonSerializer.SerializeToUtf8Bytes(_discoveryInfoProvider());
                    await _udp.SendAsync(resp, resp.Length, res.RemoteEndPoint).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        DropActiveClient();
        try { _listener?.Stop(); } catch { }
        try { _udp?.Dispose(); } catch { }
        _cts?.Dispose();
        _writeLock.Dispose();
    }
}
