using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RemotePcHost.Security;

namespace RemotePcHost.Services;

public enum ActiveTransportKind
{
    None,
    LocalWifi,
    CloudWss
}

public sealed record TransportSnapshot(
    bool CloudReady,
    string CloudBroker,
    bool ClientConnected,
    ActiveTransportKind ActiveTransport,
    string ClientLabel,
    string LanIp,
    int LanPort,
    int VideoHeight,
    int JpegQuality,
    int TargetFps,
    int Fps);

/// <summary>
/// Orchestrates encrypted dual-path communication (Local LAN WebSocket + Cloud MQTT-over-WSS),
/// adaptive desktop video streaming with ACK backpressure and chunking, input injection,
/// smart IME hints, and virtual microphone voice streaming.
/// </summary>
public sealed class HybridTransportServer : IDisposable
{
    private const int CloudMaxChunkBytes = 28 * 1024; // Reliable under all public MQTT WSS broker limits

    private readonly CryptoSession _crypto;
    private readonly ScreenStreamer _streamer;
    private readonly InputInjector _input;
    private readonly VirtualMicrophoneManager _mic;
    private readonly LocalWebSocketServer _lanServer;
    private readonly MqttOverWebSocketClient _cloudMqtt;
    private readonly WindowsNotificationWatcher _notifWatcher;
    private readonly string _notifyH2cTopic;
    private readonly string _notifyC2hTopic;

    private sealed record ForwardedNotif(long Id, string App, string Title, string Body, long TsMs);
    private readonly object _notifLock = new();
    private readonly List<ForwardedNotif> _recentNotifs = new(32);
    private long _lastNotifId;

    private CancellationTokenSource? _cts;
    private ActiveTransportKind _activeTransport = ActiveTransportKind.None;
    private string _clientLabel = "";
    private long _lastClientSeenMs;
    private uint _lastSentFrameId;
    private uint _lastAckedFrameId;
    private long _lastFrameSentMs;
    private int _framesInSecond;
    private int _currentFps;
    private long _fpsWindowStartMs;
    private bool _streamEnabled;
    private volatile bool _clientPaused;

    public string LanIp { get; private set; }
    public HostConfig Config => _crypto.Config;
    public VirtualMicrophoneManager MicManager => _mic;

    public event Action<TransportSnapshot>? StateChanged;
    public event Action<string>? LogLine;

    public HybridTransportServer(
        CryptoSession crypto,
        ScreenStreamer streamer,
        InputInjector input,
        VirtualMicrophoneManager mic)
    {
        _crypto = crypto;
        _streamer = streamer;
        _input = input;
        _mic = mic;
        LanIp = DetectBestLanIpv4();

        _lanServer = new LocalWebSocketServer(
            crypto.Config.LanPort,
            crypto.Config.DiscoveryPort,
            () => new
            {
                id = _crypto.Config.ChannelId,
                name = Environment.MachineName,
                ip = LanIp,
                port = _crypto.Config.LanPort
            });

        string c2h = $"rpc/v1/{crypto.Config.ChannelId}/c2h";
        string h2c = $"rpc/v1/{crypto.Config.ChannelId}/h2c";
        _notifyC2hTopic = $"rpc/v1/{crypto.Config.ChannelId}/notify_c2h";
        _notifyH2cTopic = $"rpc/v1/{crypto.Config.ChannelId}/notify_h2c";
        string clientId = $"rpchost_{crypto.Config.ChannelId}_{Environment.TickCount64 & 0xFFFF:x4}";
        _cloudMqtt = new MqttOverWebSocketClient(clientId, new[] { c2h, _notifyC2hTopic }, h2c);

        _notifWatcher = new WindowsNotificationWatcher();
        _notifWatcher.NotificationCaptured += item =>
        {
            if (_crypto.Config.ForwardPcNotifications)
            {
                _ = DispatchPcNotificationAsync(item.App, item.Title, item.Body, item.ArrivalUnixMs, isTest: false);
            }
        };

        _lanServer.MessageReceived += bytes => HandleIncomingPacket(bytes, ActiveTransportKind.LocalWifi);
        _lanServer.UdpNotifySyncReceived += (bytes, ep) => HandleNotifySyncPacket(bytes, ep);
        _lanServer.ClientStateChanged += (connected, ip) =>
        {
            if (connected)
            {
                Log($"LAN клиент подключён ({ip})");
            }
            else if (_activeTransport == ActiveTransportKind.LocalWifi)
            {
                DisconnectClient("LAN отключён");
            }
        };

        _cloudMqtt.TopicMessageReceived += (topic, bytes) =>
        {
            if (topic == _notifyC2hTopic)
                HandleNotifySyncPacket(bytes, udpTarget: null);
            else
                HandleIncomingPacket(bytes, ActiveTransportKind.CloudWss);
        };
        _cloudMqtt.ConnectionStateChanged += (ok, broker) =>
        {
            Log(ok ? $"Облачный канал WSS подключён ({broker})" : "Облачный канал WSS переподключается...");
            RaiseState();
        };

        _mic.PcAppMicUsageChanged += (active, apps) =>
        {
            if (_activeTransport == ActiveTransportKind.None) return;
            _ = SendEncryptedJsonAsync(new
            {
                t = "mic_alert",
                active,
                apps
            });
        };
    }

    public string GetPairingUri()
    {
        LanIp = DetectBestLanIpv4();
        return _crypto.BuildPairingUri(LanIp);
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();

        _lanServer.Start();
        Log($"Локальный Wi-Fi сервер запущен: ws://{LanIp}:{_crypto.Config.LanPort}/ws");

        _cloudMqtt.Start();
        _mic.StartMicUsageWatcher();
        _notifWatcher.Start();

        _ = Task.Run(() => VideoLoopAsync(_cts.Token));
        _ = Task.Run(() => WatchdogLoopAsync(_cts.Token));
        RaiseState();
    }

    public TransportSnapshot GetSnapshot() => new(
        CloudReady: _cloudMqtt.IsConnected,
        CloudBroker: _cloudMqtt.ActiveBroker,
        ClientConnected: _activeTransport != ActiveTransportKind.None,
        ActiveTransport: _activeTransport,
        ClientLabel: _clientLabel,
        LanIp: LanIp,
        LanPort: _crypto.Config.LanPort,
        VideoHeight: _streamer.CurrentSettings.MaxHeight,
        JpegQuality: _streamer.CurrentSettings.JpegQuality,
        TargetFps: _streamer.CurrentSettings.TargetFps,
        Fps: _currentFps);

    private void HandleIncomingPacket(byte[] raw, ActiveTransportKind source)
    {
        if (!_crypto.TryDecrypt(raw, out PacketType type, out _, out byte[] plain))
            return;

        _lastClientSeenMs = Environment.TickCount64;

        // Automatically upgrade from Cloud to LAN if LAN receives an authenticated packet
        if (_activeTransport != source && (_activeTransport == ActiveTransportKind.None || source == ActiveTransportKind.LocalWifi))
        {
            _activeTransport = source;
            _streamEnabled = true;
            _mic.IsClientConnected = true;
            _lastAckedFrameId = _lastSentFrameId;
            _streamer.ForceNextFrame();
            Log(source == ActiveTransportKind.LocalWifi ? "Активный канал: Домашний Wi-Fi (LAN)" : "Активный канал: Мобильный интернет (WSS)");
            RaiseState();
        }

        switch (type)
        {
            case PacketType.ControlJson:
                HandleControlJson(plain, source);
                break;

            case PacketType.AudioMic:
                _mic.FeedAudioPacket(plain);
                break;

            case PacketType.PingPong:
                _ = SendPacketAsync(PacketType.PingPong, plain, source);
                break;
        }
    }

    private void HandleControlJson(byte[] utf8, ActiveTransportKind source)
    {
        try
        {
            using var doc = JsonDocument.Parse(utf8);
            var root = doc.RootElement;
            if (!root.TryGetProperty("t", out var tProp)) return;
            string t = tProp.GetString() ?? "";

            switch (t)
            {
                case "hello":
                {
                    _clientLabel = root.TryGetProperty("dev", out var dev) ? (dev.GetString() ?? "Android") : "Android";
                    _activeTransport = source;
                    _streamEnabled = true;
                    _clientPaused = false;
                    _mic.IsClientConnected = true;
                    _lastSentFrameId = 0;
                    _lastAckedFrameId = 0;
                    _streamer.ForceNextFrame();

                    JsonElement vqProp = default, vfpsProp = default;
                    bool hasClientVideo = root.TryGetProperty("vh", out var vhProp)
                                       && root.TryGetProperty("vq", out vqProp)
                                       && root.TryGetProperty("vfps", out vfpsProp);
                    if (hasClientVideo)
                    {
                        _streamer.SetVideoConfig(vhProp.GetInt32(), vqProp.GetInt32(), vfpsProp.GetInt32());
                    }
                    else if (source == ActiveTransportKind.LocalWifi)
                    {
                        _streamer.SetVideoConfig(1080, 75, 30);
                    }
                    else
                    {
                        _streamer.SetVideoConfig(720, 55, 15);
                    }

                    LanIp = DetectBestLanIpv4();
                    var vs = _streamer.CurrentSettings;
                    var bounds = _streamer.GetActiveScreenBounds();
                    var micStatus = _mic.GetStatus();
                    _ = SendEncryptedJsonAsync(new
                    {
                        t = "hello_ack",
                        pc = Environment.MachineName,
                        w = bounds.Width,
                        h = bounds.Height,
                        lan = $"{LanIp}:{_crypto.Config.LanPort}",
                        monitors = _streamer.MonitorCount,
                        mon = _streamer.MonitorIndex,
                        transport = source == ActiveTransportKind.LocalWifi ? "lan" : "cloud",
                        vh = vs.MaxHeight,
                        vq = vs.JpegQuality,
                        vfps = vs.TargetFps,
                        hasVbCable = micStatus.HasVbCable,
                        outputSpeakers = micStatus.OutputToSpeakers
                    }, source);

                    // Also immediately report if any PC app is currently using mic
                    string[] activeApps = VirtualMicrophoneManager.PollActiveMicApps();
                    if (activeApps.Length > 0)
                    {
                        _ = SendEncryptedJsonAsync(new { t = "mic_alert", active = true, apps = activeApps }, source);
                    }

                    Log($"Смартфон подключён: {_clientLabel} ({(source == ActiveTransportKind.LocalWifi ? "Wi-Fi" : "WSS 4G/5G")}, {vs.Summary})");
                    RaiseState();
                    break;
                }

                case "stream_pause":
                {
                    bool pause = root.TryGetProperty("pause", out var pp) && pp.GetBoolean();
                    _clientPaused = pause;
                    if (!pause)
                    {
                        _lastAckedFrameId = _lastSentFrameId;
                        _streamer.ForceNextFrame();
                    }
                    break;
                }

                case "req_keyframe":
                    _lastAckedFrameId = _lastSentFrameId;
                    _streamer.ForceNextFrame();
                    break;

                case "monitor":
                {
                    int idx = root.TryGetProperty("idx", out var ip) ? ip.GetInt32() : (_streamer.MonitorIndex + 1) % _streamer.MonitorCount;
                    _streamer.SetMonitor(idx);
                    _lastAckedFrameId = _lastSentFrameId;
                    var b = _streamer.GetActiveScreenBounds();
                    _ = SendEncryptedJsonAsync(new
                    {
                        t = "monitor_state",
                        mon = _streamer.MonitorIndex,
                        monitors = _streamer.MonitorCount,
                        w = b.Width,
                        h = b.Height
                    }, source);
                    Log($"Выбран монитор {_streamer.MonitorIndex + 1}/{_streamer.MonitorCount} ({b.Width}x{b.Height})");
                    break;
                }

                case "clip_set":
                {
                    string text = root.TryGetProperty("text", out var cp) ? (cp.GetString() ?? "") : "";
                    bool paste = root.TryGetProperty("paste", out var psp) && psp.GetBoolean();
                    InputInjector.SetClipboardText(text);
                    if (paste)
                    {
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(35).ConfigureAwait(false);
                            _input.SendHotkey(new ushort[] { 0x11, 0x56 }); // Ctrl+V
                        });
                    }
                    break;
                }

                case "clip_get":
                {
                    string text = InputInjector.GetClipboardText();
                    _ = SendEncryptedJsonAsync(new { t = "clip_data", text }, source);
                    break;
                }

                case "notif_sync":
                {
                    long since = root.TryGetProperty("since", out var sp) ? sp.GetInt64() : 0L;
                    _ = SendNotifSyncReplyAsync(since, udpTarget: null, sessionRoute: source);
                    break;
                }

                case "ack":
                    if (root.TryGetProperty("fid", out var fid))
                    {
                        uint ackId = fid.GetUInt32();
                        if (ackId > _lastAckedFrameId)
                            _lastAckedFrameId = ackId;
                    }
                    break;

                case "video_config":
                {
                    var cur = _streamer.CurrentSettings;
                    int h = root.TryGetProperty("h", out var hp) ? hp.GetInt32()
                          : (root.TryGetProperty("vh", out var vhp) ? vhp.GetInt32() : cur.MaxHeight);
                    int q = root.TryGetProperty("q", out var qp) ? qp.GetInt32()
                          : (root.TryGetProperty("vq", out var vqp) ? vqp.GetInt32() : cur.JpegQuality);
                    int fps = root.TryGetProperty("fps", out var fp) ? fp.GetInt32()
                            : (root.TryGetProperty("vfps", out var vfp) ? vfp.GetInt32() : cur.TargetFps);

                    _streamer.SetVideoConfig(h, q, fps);
                    _lastAckedFrameId = _lastSentFrameId;
                    var vs = _streamer.CurrentSettings;
                    Log($"Видео: {vs.Summary}");
                    RaiseState();
                    _ = SendEncryptedJsonAsync(new
                    {
                        t = "video_state",
                        h = vs.MaxHeight,
                        q = vs.JpegQuality,
                        fps = vs.TargetFps
                    }, source);
                    break;
                }

                case "quality":
                    if (root.TryGetProperty("profile", out var prof))
                    {
                        _streamer.SetProfile(prof.GetString() ?? "balanced");
                        _lastAckedFrameId = _lastSentFrameId;
                        RaiseState();
                    }
                    break;

                case "mouse_move":
                {
                    int dx = root.TryGetProperty("dx", out var dxp) ? dxp.GetInt32() : 0;
                    int dy = root.TryGetProperty("dy", out var dyp) ? dyp.GetInt32() : 0;
                    _input.MoveMouseRelative(dx, dy);
                    break;
                }

                case "mouse_abs":
                {
                    double x = root.GetProperty("x").GetDouble();
                    double y = root.GetProperty("y").GetDouble();
                    _input.MoveMouseAbsolute(x, y);
                    break;
                }

                case "mouse_btn":
                {
                    string btn = root.TryGetProperty("btn", out var bp) ? (bp.GetString() ?? "left") : "left";
                    string act = root.TryGetProperty("act", out var ap) ? (ap.GetString() ?? "click") : "click";
                    double? x = root.TryGetProperty("x", out var xp) ? xp.GetDouble() : null;
                    double? y = root.TryGetProperty("y", out var yp) ? yp.GetDouble() : null;
                    _input.MouseButton(btn, act, x, y);

                    if (btn == "left" && (act == "click" || act == "up" || act == "dblclick"))
                    {
                        ScheduleImeCheck();
                    }
                    break;
                }

                case "mouse_wheel":
                {
                    int dy = root.TryGetProperty("dy", out var dyp) ? dyp.GetInt32() : 0;
                    int dx = root.TryGetProperty("dx", out var dxp) ? dxp.GetInt32() : 0;
                    _input.MouseWheel(dy, dx);
                    break;
                }

                case "text":
                {
                    string s = root.TryGetProperty("s", out var sp) ? (sp.GetString() ?? "") : "";
                    _input.TypeUnicodeText(s);
                    break;
                }

                case "key":
                {
                    ushort vk = (ushort)root.GetProperty("vk").GetInt32();
                    string act = root.TryGetProperty("act", out var ap) ? (ap.GetString() ?? "tap") : "tap";
                    _input.SendVirtualKey(vk, act);
                    break;
                }

                case "hotkey":
                {
                    var arr = root.GetProperty("keys").EnumerateArray().Select(e => (ushort)e.GetInt32()).ToArray();
                    _input.SendHotkey(arr);
                    break;
                }

                case "sys":
                {
                    string act = root.TryGetProperty("act", out var ap) ? (ap.GetString() ?? "") : "";
                    _input.ExecuteSystemAction(act);
                    break;
                }

                case "mic_ctrl":
                {
                    bool on = root.TryGetProperty("on", out var onp) && onp.GetBoolean();
                    if (root.TryGetProperty("speakers", out var spk))
                        _mic.SetOutputToSpeakers(spk.GetBoolean());

                    if (on)
                    {
                        _mic.StartVoiceStream();
                        Log("Микрофон со смартфона: ВКЛЮЧЁН");
                    }
                    else
                    {
                        _mic.StopVoiceStream();
                        Log("Микрофон со смартфона: ВЫКЛЮЧЕН (восстановлен основной микрофон ПК)");
                    }

                    var st = _mic.GetStatus();
                    _ = SendEncryptedJsonAsync(new
                    {
                        t = "mic_state",
                        on,
                        hasVbCable = st.HasVbCable,
                        switched = st.IsSwitchedToVirtual,
                        speakers = st.OutputToSpeakers
                    });
                    break;
                }

                case "bye":
                    DisconnectClient("Клиент завершил сеанс");
                    break;
            }
        }
        catch
        {
            // Ignore malformed JSON
        }
    }

    private void ScheduleImeCheck()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(95).ConfigureAwait(false);
            if (_activeTransport == ActiveTransportKind.None) return;
            var hint = _input.DetectTextInputState();
            if (hint.IsTextInput)
            {
                await SendEncryptedJsonAsync(new
                {
                    t = "ime_hint",
                    show = true,
                    caretY = Math.Round(hint.CaretNormY, 3)
                }).ConfigureAwait(false);
            }
        });
    }

    private async Task VideoLoopAsync(CancellationToken ct)
    {
        _fpsWindowStartMs = Environment.TickCount64;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_activeTransport == ActiveTransportKind.None || !_streamEnabled || _clientPaused)
                {
                    await Task.Delay(120, ct).ConfigureAwait(false);
                    continue;
                }

                int targetFps = Math.Clamp(_streamer.CurrentSettings.TargetFps, 1, 60);
                int targetIntervalMs = Math.Max(15, (int)Math.Round(1000.0 / targetFps));
                long now = Environment.TickCount64;

                // Backpressure: allow at most 2 frames in flight, and advance ack baseline if a packet/chunk dropped
                int ackTimeoutMs = _activeTransport == ActiveTransportKind.LocalWifi ? 220 : 380;
                long inFlight = (long)_lastSentFrameId - (long)_lastAckedFrameId;
                if (inFlight >= 2)
                {
                    if ((now - _lastFrameSentMs) < ackTimeoutMs)
                    {
                        await Task.Delay(8, ct).ConfigureAwait(false);
                        continue;
                    }
                    _lastAckedFrameId = _lastSentFrameId;
                }

                bool forceKey = _lastSentFrameId == 0;
                var frame = _streamer.Capture(forceSend: forceKey);
                if (frame.ContentChanged && frame.JpegBytes.Length > 0)
                {
                    bool sent;
                    if (frame.IsDeltaTile)
                    {
                        byte[] tilePayload = ScreenStreamer.SerializeTilePayload(frame);
                        sent = await SendPacketAsync(PacketType.VideoTile, tilePayload, _activeTransport, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        byte[] payload = ScreenStreamer.SerializeFramePayload(frame);
                        sent = await SendVideoPayloadAsync(frame.FrameId, payload, ct).ConfigureAwait(false);
                    }
                    if (sent)
                    {
                        _lastSentFrameId = frame.FrameId;
                        _lastFrameSentMs = Environment.TickCount64;
                        _framesInSecond++;
                    }
                }

                long elapsedWindow = Environment.TickCount64 - _fpsWindowStartMs;
                if (elapsedWindow >= 1000)
                {
                    _currentFps = _framesInSecond;
                    _framesInSecond = 0;
                    _fpsWindowStartMs = Environment.TickCount64;
                    RaiseState();
                }

                int spent = (int)(Environment.TickCount64 - now);
                int sleep = Math.Max(4, targetIntervalMs - spent);
                await Task.Delay(sleep, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                await Task.Delay(150, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> SendVideoPayloadAsync(uint frameId, byte[] payload, CancellationToken ct)
    {
        var transport = _activeTransport;
        if (transport == ActiveTransportKind.LocalWifi || payload.Length <= CloudMaxChunkBytes)
        {
            return await SendPacketAsync(PacketType.VideoFrame, payload, transport, ct).ConfigureAwait(false);
        }

        // Chunk large frames for Cloud MQTT WSS reliability
        int totalChunks = (payload.Length + CloudMaxChunkBytes - 1) / CloudMaxChunkBytes;
        for (int i = 0; i < totalChunks; i++)
        {
            int offset = i * CloudMaxChunkBytes;
            int len = Math.Min(CloudMaxChunkBytes, payload.Length - offset);
            byte[] chunk = new byte[8 + len];
            BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(0, 4), frameId);
            BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(4, 2), (ushort)i);
            BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(6, 2), (ushort)totalChunks);
            Buffer.BlockCopy(payload, offset, chunk, 8, len);

            if (!await SendPacketAsync(PacketType.VideoChunk, chunk, transport, ct).ConfigureAwait(false))
                return false;
        }
        return true;
    }

    private async Task WatchdogLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(2000, ct).ConfigureAwait(false);
                if (_activeTransport != ActiveTransportKind.None)
                {
                    if (Environment.TickCount64 - _lastClientSeenMs > 18_000)
                    {
                        DisconnectClient("Тайм-аут клиента");
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    public void ForceDisconnectClient()
    {
        if (_activeTransport == ActiveTransportKind.None) return;
        _ = SendEncryptedJsonAsync(new { t = "bye" });
        DisconnectClient("Сеанс принудительно завершён на ПК");
    }

    private void DisconnectClient(string reason)
    {
        if (_activeTransport == ActiveTransportKind.None) return;
        _activeTransport = ActiveTransportKind.None;
        _streamEnabled = false;
        _clientPaused = false;
        _mic.IsClientConnected = false;
        _clientLabel = "";
        _currentFps = 0;
        _input.ReleaseAllHeldInputs();
        _mic.StopVoiceStream();
        _crypto.ResetReplayWindow();
        Log(reason);
        RaiseState();
    }

    private Task<bool> SendEncryptedJsonAsync(object obj, ActiveTransportKind? overrideTransport = null)
    {
        byte[] utf8 = JsonSerializer.SerializeToUtf8Bytes(obj);
        return SendPacketAsync(PacketType.ControlJson, utf8, overrideTransport ?? _activeTransport);
    }

    private Task<bool> SendPacketAsync(PacketType type, ReadOnlySpan<byte> plaintext, ActiveTransportKind transport, CancellationToken ct = default)
    {
        if (transport == ActiveTransportKind.None) return Task.FromResult(false);
        byte[] encrypted = _crypto.Encrypt(type, plaintext);
        return transport == ActiveTransportKind.LocalWifi
            ? _lanServer.SendBinaryAsync(encrypted, ct)
            : _cloudMqtt.PublishAsync(encrypted, ct);
    }

    public Task<bool> ProbeCloudAsync() => _cloudMqtt.ProbeOnceAsync(6000);

    public void SetForwardNotifications(bool enable)
    {
        _crypto.Config.ForwardPcNotifications = enable;
        CryptoSession.SaveConfig(_crypto.Config);
        Log(enable ? "Пересылка уведомлений Windows на смартфон: ВКЛЮЧЕНА" : "Пересылка уведомлений Windows на смартфон: ОТКЛЮЧЕНА");
    }

    public Task SendTestNotificationAsync()
    {
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return DispatchPcNotificationAsync(
            "RemotePC",
            "Тестовое уведомление с ПК",
            "Связь работает! Уведомления приходят даже при выключенном экране.",
            nowMs,
            isTest: true);
    }

    private async Task DispatchPcNotificationAsync(string app, string title, string body, long tsMs, bool isTest)
    {
        long id;
        lock (_notifLock)
        {
            long candidate = Math.Max(tsMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            id = Math.Max(candidate, _lastNotifId + 1);
            _lastNotifId = id;

            long cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 3_600_000L;
            _recentNotifs.RemoveAll(n => n.TsMs < cutoff);
            _recentNotifs.Add(new ForwardedNotif(id, app, title, body, tsMs > 0 ? tsMs : id));
            while (_recentNotifs.Count > 30)
                _recentNotifs.RemoveAt(0);
        }

        var payload = new
        {
            t = "pc_notif",
            id,
            app,
            title,
            body,
            ts = tsMs > 0 ? tsMs : id
        };
        byte[] enc = _crypto.EncryptJson(payload);

        var tCloud = _cloudMqtt.PublishToTopicAsync(_notifyH2cTopic, enc);
        var tLanUdp = _lanServer.SendUdpNotificationAsync(enc);
        Task tSession = _activeTransport != ActiveTransportKind.None
            ? SendEncryptedJsonAsync(payload)
            : Task.CompletedTask;

        await Task.WhenAll(tCloud, tLanUdp, tSession).ConfigureAwait(false);
        Log($"{(isTest ? "Тест" : "Уведомление")} -> смартфон [{app}]: {title}");
    }

    private void HandleNotifySyncPacket(byte[] raw, IPEndPoint? udpTarget)
    {
        if (!_crypto.TryDecrypt(raw, out PacketType type, out _, out byte[] plain))
            return;
        if (type != PacketType.ControlJson)
            return;

        try
        {
            using var doc = JsonDocument.Parse(plain);
            var root = doc.RootElement;
            if (!root.TryGetProperty("t", out var tProp)) return;
            if (tProp.GetString() != "notif_sync") return;

            long since = root.TryGetProperty("since", out var sp) ? sp.GetInt64() : 0L;
            _ = SendNotifSyncReplyAsync(since, udpTarget, sessionRoute: null);
        }
        catch { }
    }

    private async Task SendNotifSyncReplyAsync(long since, IPEndPoint? udpTarget, ActiveTransportKind? sessionRoute)
    {
        if (since <= 0)
        {
            long latest;
            lock (_notifLock)
            {
                latest = Math.Max(_lastNotifId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                _lastNotifId = latest;
            }
            byte[] cursorEnc = _crypto.EncryptJson(new { t = "notif_cursor", latest });
            if (sessionRoute != null)
                await SendPacketAsync(PacketType.ControlJson, JsonSerializer.SerializeToUtf8Bytes(new { t = "notif_cursor", latest }), sessionRoute.Value).ConfigureAwait(false);
            else if (udpTarget != null)
                await _lanServer.SendUdpNotificationAsync(cursorEnc, udpTarget).ConfigureAwait(false);
            else
                await _cloudMqtt.PublishToTopicAsync(_notifyH2cTopic, cursorEnc).ConfigureAwait(false);
            return;
        }

        if (!_crypto.Config.ForwardPcNotifications) return;

        List<ForwardedNotif> missed;
        lock (_notifLock)
        {
            missed = _recentNotifs.Where(n => n.Id > since).ToList();
        }

        foreach (var n in missed)
        {
            var payload = new
            {
                t = "pc_notif",
                id = n.Id,
                app = n.App,
                title = n.Title,
                body = n.Body,
                ts = n.TsMs
            };
            if (sessionRoute != null)
            {
                await SendEncryptedJsonAsync(payload, sessionRoute.Value).ConfigureAwait(false);
            }
            else if (udpTarget != null)
            {
                await _lanServer.SendUdpNotificationAsync(_crypto.EncryptJson(payload), udpTarget).ConfigureAwait(false);
            }
            else
            {
                await _cloudMqtt.PublishToTopicAsync(_notifyH2cTopic, _crypto.EncryptJson(payload)).ConfigureAwait(false);
            }
        }
    }

    private void RaiseState() => StateChanged?.Invoke(GetSnapshot());
    private void Log(string msg) => LogLine?.Invoke($"[{DateTime.Now:HH:mm:ss}] {msg}");

    public static string DetectBestLanIpv4()
    {
        string? fallback = null;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                string desc = (ni.Name + " " + ni.Description).ToLowerInvariant();
                if (desc.Contains("vethernet") || desc.Contains("virtual") || desc.Contains("vmware") || desc.Contains("tailscale"))
                    continue;

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    string ip = ua.Address.ToString();
                    if (ip.StartsWith("192.168.") && !ip.StartsWith("192.168.137."))
                        return ip;
                    if (ip.StartsWith("10.") || ip.StartsWith("172."))
                        fallback ??= ip;
                }
            }
        }
        catch { }
        return fallback ?? "127.0.0.1";
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _notifWatcher.Dispose();
        _mic.Dispose();
        _lanServer.Dispose();
        _cloudMqtt.Dispose();
        _streamer.Dispose();
        _cts?.Dispose();
    }
}
