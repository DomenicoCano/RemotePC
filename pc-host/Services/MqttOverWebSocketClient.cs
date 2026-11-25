using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;

namespace RemotePcHost.Services;

/// <summary>
/// Lightweight, zero-dependency MQTT 3.1.1 over WebSocket (WSS) client with automatic failover
/// across multiple TLS brokers (EMQX 8084 and HiveMQ 8884).
/// </summary>
public sealed class MqttOverWebSocketClient : IDisposable
{
    public static readonly string[] DefaultBrokers =
    {
        "wss://test.mosquitto.org:8081",
        "wss://broker.emqx.io:8084/mqtt",
        "wss://broker.hivemq.com:8884/mqtt",
        "wss://mqtt.eclipseprojects.io:443/mqtt"
    };

    private readonly string _clientId;
    private readonly string[] _subscribeTopics;
    private readonly string _publishTopic;
    private readonly string[] _brokers;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private ClientWebSocket? _ws;
    private volatile bool _isConnected;
    private string _activeBroker = "";
    private ushort _nextPacketId = 1;
    private long _lastRxTickMs;

    public bool IsConnected => _isConnected;
    public string ActiveBroker => _activeBroker;

    public event Action<byte[]>? MessageReceived;
    public event Action<string, byte[]>? TopicMessageReceived;
    public event Action<bool, string>? ConnectionStateChanged;

    public MqttOverWebSocketClient(string clientId, string subscribeTopic, string publishTopic, string[]? brokers = null)
        : this(clientId, new[] { subscribeTopic }, publishTopic, brokers)
    {
    }

    public MqttOverWebSocketClient(string clientId, string[] subscribeTopics, string publishTopic, string[]? brokers = null)
    {
        _clientId = clientId;
        _subscribeTopics = subscribeTopics.Where(t => !string.IsNullOrWhiteSpace(t)).ToArray();
        _publishTopic = publishTopic;
        _brokers = brokers ?? DefaultBrokers;
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    public Task<bool> PublishAsync(byte[] payload, CancellationToken ct = default) =>
        PublishToTopicAsync(_publishTopic, payload, ct);

    public async Task<bool> PublishToTopicAsync(string topic, byte[] payload, CancellationToken ct = default)
    {
        var ws = _ws;
        if (!_isConnected || ws == null || ws.State != WebSocketState.Open)
            return false;

        byte[] packet = BuildPublishPacket(topic, payload);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (ws.State != WebSocketState.Open) return false;
            await ws.SendAsync(new ArraySegment<byte>(packet), WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<bool> ProbeOnceAsync(int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        foreach (string url in _brokers)
        {
            try
            {
                using var ws = new ClientWebSocket();
                ws.Options.AddSubProtocol("mqtt");
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                await ws.ConnectAsync(new Uri(url), cts.Token).ConfigureAwait(false);
                byte[] connectPkt = BuildConnectPacket(_clientId + "_probe", 30);
                await ws.SendAsync(connectPkt, WebSocketMessageType.Binary, true, cts.Token).ConfigureAwait(false);

                byte[] buf = new byte[256];
                var res = await ws.ReceiveAsync(buf, cts.Token).ConfigureAwait(false);
                if (res.Count >= 4 && buf[0] == 0x20 && buf[3] == 0x00)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "ok", CancellationToken.None).ConfigureAwait(false);
                    return true;
                }
            }
            catch
            {
                // Try next broker
            }
        }
        return false;
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        int brokerIndex = 0;
        int retryDelayMs = 1500;

        while (!ct.IsCancellationRequested)
        {
            string url = _brokers[brokerIndex % _brokers.Length];
            ClientWebSocket? ws = null;
            CancellationTokenSource? pingCts = null;

            try
            {
                ws = new ClientWebSocket();
                ws.Options.AddSubProtocol("mqtt");
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(25);

                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(8));
                await ws.ConnectAsync(new Uri(url), connectTimeout.Token).ConfigureAwait(false);
                Interlocked.Exchange(ref _lastRxTickMs, Environment.TickCount64);

                byte[] connectPkt = BuildConnectPacket(_clientId, 30);
                await ws.SendAsync(connectPkt, WebSocketMessageType.Binary, true, connectTimeout.Token).ConfigureAwait(false);

                // Expect CONNACK + read loop
                var streamBuffer = new MemoryStream();
                byte[] recvBuf = new byte[65536];
                bool connAckReceived = false;

                using var msgStream = new MemoryStream();
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    msgStream.SetLength(0);
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(recvBuf, ct).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                            break;
                        if (result.Count > 0)
                            msgStream.Write(recvBuf, 0, result.Count);
                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    Interlocked.Exchange(ref _lastRxTickMs, Environment.TickCount64);
                    int chunkLen = (int)msgStream.Length;
                    if (chunkLen == 0) continue;

                    streamBuffer.Position = streamBuffer.Length;
                    streamBuffer.Write(msgStream.GetBuffer(), 0, chunkLen);

                    ProcessMqttStream(streamBuffer, ref connAckReceived, ws, url, ref pingCts, ref retryDelayMs, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Reconnect with failover
            }
            finally
            {
                pingCts?.Cancel();
                pingCts?.Dispose();
                if (_isConnected)
                {
                    _isConnected = false;
                    ConnectionStateChanged?.Invoke(false, "");
                }
                _ws = null;
                if (ws != null)
                {
                    try { ws.Dispose(); } catch { }
                }
            }

            brokerIndex++;
            try
            {
                await Task.Delay(retryDelayMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            retryDelayMs = Math.Min(retryDelayMs + 1000, 8000);
        }
    }

    private void ProcessMqttStream(
        MemoryStream buffer,
        ref bool connAckReceived,
        ClientWebSocket ws,
        string url,
        ref CancellationTokenSource? pingCts,
        ref int retryDelayMs,
        CancellationToken ct)
    {
        int totalBytes = (int)buffer.Length;
        byte[] rawBuf = buffer.GetBuffer();
        int offset = 0;

        while (offset < totalBytes)
        {
            if (totalBytes - offset < 2)
                break;

            byte header = rawBuf[offset];
            int idx = offset + 1;
            int multiplier = 1;
            int remainingLength = 0;
            bool completeVarInt = false;

            while (idx < totalBytes)
            {
                byte encoded = rawBuf[idx++];
                remainingLength += (encoded & 0x7F) * multiplier;
                if ((encoded & 0x80) == 0)
                {
                    completeVarInt = true;
                    break;
                }
                multiplier *= 128;
                if (multiplier > 128 * 128 * 128)
                {
                    // Malformed MQTT packet; reset buffer
                    buffer.SetLength(0);
                    return;
                }
            }

            if (!completeVarInt)
                break;

            int packetLen = (idx - offset) + remainingLength;
            if (totalBytes - offset < packetLen)
                break;

            byte packetType = (byte)(header >> 4);
            ReadOnlySpan<byte> body = rawBuf.AsSpan(idx, remainingLength);

            if (packetType == 2) // CONNACK
            {
                if (body.Length >= 2 && body[1] == 0x00 && !connAckReceived)
                {
                    connAckReceived = true;
                    _ws = ws;
                    _activeBroker = url;
                    _isConnected = true;
                    retryDelayMs = 1500;
                    Interlocked.Exchange(ref _lastRxTickMs, Environment.TickCount64);
                    ConnectionStateChanged?.Invoke(true, url);

                    // Subscribe to all requested topics
                    foreach (string topic in _subscribeTopics)
                    {
                        byte[] subPkt = BuildSubscribePacket(++_nextPacketId, topic);
                        _ = SendRawSafeAsync(ws, subPkt, ct);
                    }

                    // Start MQTT PINGREQ + dead-socket watchdog loop
                    pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var localPingToken = pingCts.Token;
                    _ = Task.Run(async () =>
                    {
                        byte[] pingReq = { 0xC0, 0x00 };
                        while (!localPingToken.IsCancellationRequested && ws.State == WebSocketState.Open)
                        {
                            try
                            {
                                await Task.Delay(TimeSpan.FromSeconds(20), localPingToken).ConfigureAwait(false);
                                if (Environment.TickCount64 - Interlocked.Read(ref _lastRxTickMs) > 48_000)
                                {
                                    // Half-open NAT stall detected; abort socket for instant reconnect
                                    try { ws.Abort(); } catch { }
                                    break;
                                }
                                await SendRawSafeAsync(ws, pingReq, localPingToken).ConfigureAwait(false);
                            }
                            catch { break; }
                        }
                    }, localPingToken);
                }
            }
            else if (packetType == 3) // PUBLISH
            {
                if (body.Length >= 2)
                {
                    int topicLen = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(0, 2));
                    int qos = (header >> 1) & 0x03;
                    int payloadOffset = 2 + topicLen + (qos > 0 ? 2 : 0);
                    if (2 + topicLen <= body.Length && payloadOffset <= body.Length)
                    {
                        string topic = Encoding.UTF8.GetString(body.Slice(2, topicLen));
                        byte[] payload = body.Slice(payloadOffset).ToArray();
                        TopicMessageReceived?.Invoke(topic, payload);
                        MessageReceived?.Invoke(payload);
                    }
                }
            }

            offset += packetLen;
        }

        // Compact remaining trailing partial packet bytes in-place without extra allocations
        int leftover = totalBytes - offset;
        if (leftover > 0 && offset > 0)
        {
            Buffer.BlockCopy(rawBuf, offset, rawBuf, 0, leftover);
        }
        buffer.SetLength(leftover);
    }

    private async Task SendRawSafeAsync(ClientWebSocket ws, byte[] bytes, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.SendAsync(bytes, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
        }
        catch { }
        finally
        {
            _sendLock.Release();
        }
    }

    private static byte[] BuildConnectPacket(string clientId, ushort keepAliveSeconds)
    {
        byte[] idBytes = Encoding.UTF8.GetBytes(clientId);
        using var body = new MemoryStream();
        // Protocol Name "MQTT"
        WriteMqttString(body, "MQTT");
        body.WriteByte(0x04); // Protocol Level 4 (MQTT 3.1.1)
        body.WriteByte(0x02); // Clean Session
        body.WriteByte((byte)(keepAliveSeconds >> 8));
        body.WriteByte((byte)(keepAliveSeconds & 0xFF));
        // Payload: ClientId
        body.WriteByte((byte)(idBytes.Length >> 8));
        body.WriteByte((byte)(idBytes.Length & 0xFF));
        body.Write(idBytes, 0, idBytes.Length);
        return WrapPacket(0x10, body.ToArray());
    }

    private static byte[] BuildSubscribePacket(ushort packetId, string topic)
    {
        using var body = new MemoryStream();
        body.WriteByte((byte)(packetId >> 8));
        body.WriteByte((byte)(packetId & 0xFF));
        WriteMqttString(body, topic);
        body.WriteByte(0x00); // QoS 0
        return WrapPacket(0x82, body.ToArray());
    }

    private static byte[] BuildPublishPacket(string topic, byte[] payload)
    {
        byte[] topicBytes = Encoding.UTF8.GetBytes(topic);
        int remLen = 2 + topicBytes.Length + payload.Length;
        Span<byte> varInt = stackalloc byte[4];
        int varIntLen = EncodeRemainingLength(remLen, varInt);

        byte[] pkt = new byte[1 + varIntLen + remLen];
        int p = 0;
        pkt[p++] = 0x30; // PUBLISH QoS 0
        varInt.Slice(0, varIntLen).CopyTo(pkt.AsSpan(p));
        p += varIntLen;
        pkt[p++] = (byte)(topicBytes.Length >> 8);
        pkt[p++] = (byte)(topicBytes.Length & 0xFF);
        Buffer.BlockCopy(topicBytes, 0, pkt, p, topicBytes.Length);
        p += topicBytes.Length;
        Buffer.BlockCopy(payload, 0, pkt, p, payload.Length);
        return pkt;
    }

    private static void WriteMqttString(Stream s, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        s.WriteByte((byte)(bytes.Length >> 8));
        s.WriteByte((byte)(bytes.Length & 0xFF));
        s.Write(bytes, 0, bytes.Length);
    }

    private static byte[] WrapPacket(byte fixedHeader, byte[] body)
    {
        Span<byte> varInt = stackalloc byte[4];
        int varIntLen = EncodeRemainingLength(body.Length, varInt);
        byte[] pkt = new byte[1 + varIntLen + body.Length];
        pkt[0] = fixedHeader;
        varInt.Slice(0, varIntLen).CopyTo(pkt.AsSpan(1));
        Buffer.BlockCopy(body, 0, pkt, 1 + varIntLen, body.Length);
        return pkt;
    }

    private static int EncodeRemainingLength(int length, Span<byte> dest)
    {
        int i = 0;
        do
        {
            byte encoded = (byte)(length % 128);
            length /= 128;
            if (length > 0) encoded |= 0x80;
            dest[i++] = encoded;
        } while (length > 0);
        return i;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _ws?.Dispose(); } catch { }
        _cts?.Dispose();
        _sendLock.Dispose();
    }
}
