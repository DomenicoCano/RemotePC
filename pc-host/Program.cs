using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using RemotePcHost.Security;
using RemotePcHost.Services;

namespace RemotePcHost;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\RemotePcHost_SingleInstance_v1";

    [STAThread]
    private static int Main(string[] args)
    {
        bool selfTest = args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase));
        bool printPairUri = args.Any(a => a.Equals("--print-pair-uri", StringComparison.OrdinalIgnoreCase));
        bool startMinimized = args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        int demoIdx = Array.FindIndex(args, a => a.Equals("--demo-screenshot", StringComparison.OrdinalIgnoreCase));

        if (demoIdx >= 0 && demoIdx + 1 < args.Length)
        {
            return RenderDemoScreenshot(args[demoIdx + 1]);
        }

        if (printPairUri)
        {
            var cfg = CryptoSession.LoadOrCreateConfig();
            using var crypto = new CryptoSession(cfg);
            string uri = crypto.BuildPairingUri(HybridTransportServer.DetectBestLanIpv4());
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "remotepc_pair_uri.txt"), uri, Encoding.UTF8);
            return 0;
        }

        if (selfTest)
        {
            return RunSelfTest();
        }

        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            if (!startMinimized)
            {
                MessageBox.Show(
                    "RemotePC Host уже запущен (проверьте значок в системном трее рядом с часами).",
                    "RemotePC Host",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            return 0;
        }

        ApplicationConfiguration.Initialize();

        var config = CryptoSession.LoadOrCreateConfig();
        using var cryptoSession = new CryptoSession(config);
        using var streamer = new ScreenStreamer();
        var input = new InputInjector(streamer.GetActiveScreenBounds);
        using var mic = new VirtualMicrophoneManager(config);
        using var server = new HybridTransportServer(cryptoSession, streamer, input, mic);

        void EmergencyCleanup(string? crashText)
        {
            try { input.ReleaseAllHeldInputs(); } catch { }
            try { mic.StopVoiceStream(); } catch { }
            if (!string.IsNullOrEmpty(crashText))
            {
                try
                {
                    Directory.CreateDirectory(CryptoSession.ConfigDir);
                    File.AppendAllText(
                        Path.Combine(CryptoSession.ConfigDir, "crash.log"),
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {crashText}{Environment.NewLine}",
                        Encoding.UTF8);
                }
                catch { }
            }
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => EmergencyCleanup("ThreadException: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => EmergencyCleanup("UnhandledException: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            EmergencyCleanup("UnobservedTask: " + e.Exception);
            e.SetObserved();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => EmergencyCleanup(null);

        // Save latest pairing URI to temp for convenient diagnostics / 1-click USB pairing
        try
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "remotepc_pair_uri.txt"), server.GetPairingUri(), Encoding.UTF8);
        }
        catch { }

        server.Start();

        using var form = new MainForm(server, startMinimized);
        Application.Run(form);
        EmergencyCleanup(null);
        return 0;
    }

    private static int RunSelfTest()
    {
        var sb = new StringBuilder();
        bool allOk = true;

        void Check(string name, bool ok, string detail)
        {
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}: {detail}");
            if (!ok) allOk = false;
        }

        try
        {
            // 1. AES-256-GCM encryption + tamper + replay rejection
            var cfg = CryptoSession.LoadOrCreateConfig();
            using var alice = new CryptoSession(cfg);
            using var bob = new CryptoSession(cfg);

            byte[] msg = Encoding.UTF8.GetBytes("Привет RemotePC 2560x1440!");
            byte[] enc = alice.Encrypt(PacketType.ControlJson, msg);
            bool decOk = bob.TryDecrypt(enc, out var type, out uint seq, out byte[] plain);
            Check("AES-256-GCM Roundtrip", decOk && type == PacketType.ControlJson && seq == 1 && plain.SequenceEqual(msg),
                $"len={enc.Length}, seq={seq}");

            // Replay must be rejected
            bool replayRejected = !bob.TryDecrypt(enc, out _, out _, out _);
            Check("Anti-Replay Window", replayRejected, "duplicate packet rejected");

            // New client session (different salt, seq=1) must be accepted immediately
            using var aliceReconnect = new CryptoSession(cfg);
            byte[] enc2 = aliceReconnect.Encrypt(PacketType.ControlJson, msg);
            bool reconnectOk = bob.TryDecrypt(enc2, out _, out uint seq2, out _);
            Check("Reconnect Salt Reset", reconnectOk && seq2 == 1, $"seq2={seq2}");

            // Tampered ciphertext must be rejected
            byte[] tampered = (byte[])enc.Clone();
            tampered[^1] ^= 0x55;
            bool tamperRejected = !bob.TryDecrypt(tampered, out _, out _, out _);
            Check("GCM Auth Tag Verification", tamperRejected, "tampered tag rejected");

            // 2. Screen capture + dynamic video_config test
            using var streamer = new ScreenStreamer();
            streamer.SetVideoConfig(480, 20, 5);
            var frameLow = streamer.Capture(forceSend: true);
            streamer.SetVideoConfig(1440, 90, 60);
            var sw = Stopwatch.StartNew();
            var frameHigh = streamer.Capture(forceSend: true);
            sw.Stop();
            Check("Screen Capture & Video Config",
                frameLow.EncodedHeight == 480 && frameHigh.EncodedHeight >= 1080
                && frameLow.JpegBytes.Length > 2000 && frameHigh.JpegBytes.Length > frameLow.JpegBytes.Length,
                $"low={frameLow.EncodedWidth}x{frameLow.EncodedHeight}({frameLow.JpegBytes.Length / 1024}KB), high={frameHigh.EncodedWidth}x{frameHigh.EncodedHeight}({frameHigh.JpegBytes.Length / 1024}KB), time={sw.ElapsedMilliseconds}ms");

            // 2b. Delta-Tile (Dirty-Rect) encoding + 29-byte header validation
            using var tileStreamer = new ScreenStreamer();
            tileStreamer.SetVideoConfig(720, 60, 15);
            using var syntheticBmp = new Bitmap(1280, 720, PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(syntheticBmp))
            {
                g.Clear(Color.Navy);
                g.FillRectangle(Brushes.Gold, 40, 40, 200, 100);
            }
            var kf = tileStreamer.EncodeBitmapForTest(syntheticBmp, forceKey: true);
            using (var g2 = Graphics.FromImage(syntheticBmp))
            {
                g2.FillRectangle(Brushes.LimeGreen, 500, 300, 80, 50);
            }
            var df = tileStreamer.EncodeBitmapForTest(syntheticBmp, forceKey: false);
            byte[] tilePkt = ScreenStreamer.SerializeTilePayload(df);
            Check("Delta Tile (Dirty-Rect)",
                !kf.IsDeltaTile && df.IsDeltaTile && df.BaseFrameId == kf.FrameId
                && df.TileW < 200 && df.TileH < 150 && tilePkt.Length == 29 + df.JpegBytes.Length,
                $"key={kf.JpegBytes.Length}B, tile={df.TileW}x{df.TileH}@({df.TileX},{df.TileY})={df.JpegBytes.Length}B, pkt={tilePkt.Length}B");

            // 2c. Input stuck-release safeguard test
            var testInj = new InputInjector();
            testInj.MouseButton("left", "down");
            testInj.SendVirtualKey(0x11, "down");
            int heldBefore = testInj.HeldInputCount;
            testInj.ReleaseAllHeldInputs();
            Check("Input Release Safeguard", heldBefore == 2 && testInj.HeldInputCount == 0,
                $"before={heldBefore}, after={testInj.HeldInputCount}");

            // 3. IMA-ADPCM decode sanity
            byte[] adpcmSample = { 1, 0, 0, 10, 4, 0, 0x12, 0x34 };
            byte[] pcm = ImaAdpcmCodec.DecodePacketToPcm16(adpcmSample);
            Check("IMA-ADPCM Codec", pcm.Length == 8, $"decoded={pcm.Length} bytes");

            // 4. Audio endpoint detection
            using var mic = new VirtualMicrophoneManager(cfg);
            var micStatus = mic.GetStatus();
            Check("Audio Enumeration", !string.IsNullOrEmpty(micStatus.BaselineMicName),
                $"baseline={micStatus.BaselineMicName}, vbCable={micStatus.HasVbCable}");

            // 5. Cloud MQTT-over-WSS probe
            using var mqtt = new MqttOverWebSocketClient("rpctest_" + Guid.NewGuid().ToString("N")[..8], "rpc/test/in", "rpc/test/out");
            bool cloudOk = mqtt.ProbeOnceAsync(6000).GetAwaiter().GetResult();
            Check("Cloud WSS Broker", cloudOk, cloudOk ? "CONNACK OK" : "unreachable");

            // 6. Pairing URI
            string lanIp = HybridTransportServer.DetectBestLanIpv4();
            string pairUri = alice.BuildPairingUri(lanIp);
            Check("Pairing URI", pairUri.StartsWith("remotepc://pair?"), $"lan={lanIp}:{cfg.LanPort}");

            // 7. Multi-salt simultaneous anti-replay sessions (session + bg notification service)
            using var senderBg = new CryptoSession(cfg);
            using var senderUi = new CryptoSession(cfg);
            byte[] bgPkt1 = senderBg.EncryptJson(new { t = "notif_sync", since = 10 });
            byte[] uiPkt1 = senderUi.EncryptJson(new { t = "ping" });
            byte[] bgPkt2 = senderBg.EncryptJson(new { t = "notif_sync", since = 20 });
            bool multiOk = bob.TryDecrypt(bgPkt1, out _, out _, out _)
                        && bob.TryDecrypt(uiPkt1, out _, out _, out _)
                        && bob.TryDecrypt(bgPkt2, out _, out _, out _)
                        && !bob.TryDecrypt(bgPkt1, out _, out _, out _);
            Check("Multi-Salt Anti-Replay", multiOk, "2 concurrent client salts + replay reject");

            // 8. Toast XML & AUMID parser
            byte[] toastXml = Encoding.UTF8.GetBytes(
                "<toast><visual><binding template=\"ToastGeneric\"><text>Контакт</text><text>Привет с ПК!</text><text placement=\"attribution\">Telegram</text></binding></visual></toast>");
            var (tTitle, tBody) = WindowsNotificationWatcher.ParseToastXml(toastXml);
            string appPretty = WindowsNotificationWatcher.FormatAppName("Telegram.TelegramDesktop.Store");
            Check("Toast XML & AppName Parser",
                tTitle == "Контакт" && tBody == "Привет с ПК!" && appPretty == "Telegram",
                $"app={appPretty}, title={tTitle}, body={tBody}");

            // 9. Windows WPN SQLite Reader (winsqlite3.dll)
            using var watcher = new WindowsNotificationWatcher();
            bool wpnOk = watcher.TryReadCurrentMaxOrder(out long maxOrder, out string wpnDetail);
            Check("WPN SQLite Reader (winsqlite3)", wpnOk && maxOrder >= 0, wpnDetail);
        }
        catch (Exception ex)
        {
            Check("Unhandled Exception", false, ex.ToString());
        }

        string report = sb.ToString();
        string outPath = Path.Combine(Path.GetTempPath(), "remotepc_selftest.txt");
        File.WriteAllText(outPath, report, Encoding.UTF8);
        return allOk ? 0 : 1;
    }

    private static int RenderDemoScreenshot(string outputPath)
    {
        ApplicationConfiguration.Initialize();
        var demoCfg = new HostConfig
        {
            ChannelId = "01020304demo5678",
            KeyBase64Url = CryptoSession.ToBase64Url(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            ForwardPcNotifications = true
        };
        using var cryptoSession = new CryptoSession(demoCfg);
        using var streamer = new ScreenStreamer();
        var input = new InputInjector(streamer.GetActiveScreenBounds);
        using var mic = new VirtualMicrophoneManager(demoCfg);
        using var server = new HybridTransportServer(cryptoSession, streamer, input, mic);
        using var form = new MainForm(server, startMinimized: false);
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-32000, -32000);
        form.ShowInTaskbar = false;
        form.Show();
        Application.DoEvents();
        form.ApplyDemoStateForScreenshot();
        Application.DoEvents();

        using var bmp = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(form.BackColor);
        }
        foreach (Control ctl in form.Controls)
        {
            ctl.DrawToBitmap(bmp, ctl.Bounds);
        }

        string? dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        bmp.Save(outputPath, ImageFormat.Png);
        form.DisposeForDemo();
        return 0;
    }
}
