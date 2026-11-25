using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using Microsoft.Win32;
using QRCoder;
using RemotePcHost.Security;
using RemotePcHost.Services;

namespace RemotePcHost;

public sealed class MainForm : Form
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "RemotePcHost";

    private readonly HybridTransportServer _server;
    private readonly bool _startMinimized;
    private readonly NotifyIcon _trayIcon;
    private readonly Icon _appIcon;
    private bool _reallyExit;
    private bool _balloonShown;

    private readonly Label _lblCloudStatus;
    private readonly Label _lblLanStatus;
    private readonly Label _lblClientStatus;
    private readonly Button _btnDisconnectClient;
    private readonly PictureBox _qrBox;
    private readonly TextBox _txtPairUri;
    private readonly Label _lblMicStatus;
    private readonly Button _btnInstallVbCable;
    private readonly CheckBox _chkSpeakers;
    private readonly CheckBox _chkForwardNotifs;
    private readonly CheckBox _chkAutoStart;
    private readonly TextBox _txtLog;

    public MainForm(HybridTransportServer server, bool startMinimized)
    {
        _server = server;
        _startMinimized = startMinimized;
        _appIcon = CreateAppIcon();

        Text = "RemotePC Host — Удалённый доступ к ПК";
        Icon = _appIcon;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ClientSize = new Size(820, 680);
        BackColor = Color.FromArgb(18, 20, 27);
        ForeColor = Color.FromArgb(236, 240, 248);
        Font = new Font("Segoe UI", 9.5f);

        // Header
        var lblTitle = new Label
        {
            Text = "RemotePC Host",
            Font = new Font("Segoe UI", 16f, FontStyle.Bold),
            ForeColor = Color.FromArgb(96, 210, 255),
            Location = new Point(22, 16),
            AutoSize = true
        };
        var lblSub = new Label
        {
            Text = "Управление ПК со смартфона по домашнему Wi-Fi и мобильному интернету (AES-256-GCM)",
            ForeColor = Color.FromArgb(158, 168, 188),
            Location = new Point(24, 48),
            AutoSize = true
        };
        Controls.Add(lblTitle);
        Controls.Add(lblSub);

        // Left Card: Connection Status + QR Pairing
        var cardLeft = CreateCard(22, 82, 376, 456);
        Controls.Add(cardLeft);

        cardLeft.Controls.Add(CreateSectionHeader("Привязка смартфона", 16, 14));

        _qrBox = new PictureBox
        {
            Location = new Point(78, 46),
            Size = new Size(220, 220),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.White,
            Padding = new Padding(8)
        };
        cardLeft.Controls.Add(_qrBox);

        _txtPairUri = new TextBox
        {
            Location = new Point(16, 276),
            Size = new Size(344, 26),
            ReadOnly = true,
            BackColor = Color.FromArgb(14, 16, 22),
            ForeColor = Color.FromArgb(170, 215, 255),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 8.5f)
        };
        cardLeft.Controls.Add(_txtPairUri);

        var btnCopyUri = CreateButton("Копировать код", 16, 312, 166, 36, Color.FromArgb(42, 54, 78));
        btnCopyUri.Click += (_, _) =>
        {
            Clipboard.SetText(_txtPairUri.Text);
            AppendLog("Код привязки скопирован в буфер обмена.");
        };
        cardLeft.Controls.Add(btnCopyUri);

        var btnUsbPair = CreateButton("Привязать по USB", 194, 312, 166, 36, Color.FromArgb(24, 122, 198));
        btnUsbPair.Click += async (_, _) => await PairViaUsbAsync();
        cardLeft.Controls.Add(btnUsbPair);

        var btnNewKey = CreateButton("Сбросить ключ безопасности", 16, 356, 344, 32, Color.FromArgb(62, 38, 46));
        btnNewKey.Click += (_, _) => ResetKeyAndRefresh();
        cardLeft.Controls.Add(btnNewKey);

        // Right Top Card: Network & Client Status
        var cardStatus = CreateCard(414, 82, 384, 138);
        Controls.Add(cardStatus);
        cardStatus.Controls.Add(CreateSectionHeader("Состояние связи", 16, 12));

        _lblCloudStatus = CreateStatusLabel("Облачный WSS (4G/5G): подключение...", 16, 42);
        _lblLanStatus = CreateStatusLabel("Домашний Wi-Fi: запуск...", 16, 68);
        _lblClientStatus = CreateStatusLabel("Смартфон: ожидание подключения", 16, 94);
        _btnDisconnectClient = CreateButton("Отключить сеанс", 244, 8, 126, 26, Color.FromArgb(124, 42, 52));
        _btnDisconnectClient.Visible = false;
        _btnDisconnectClient.Click += (_, _) => _server.ForceDisconnectClient();
        cardStatus.Controls.Add(_lblCloudStatus);
        cardStatus.Controls.Add(_lblLanStatus);
        cardStatus.Controls.Add(_lblClientStatus);
        cardStatus.Controls.Add(_btnDisconnectClient);

        // Right Middle Card: Virtual Microphone
        var cardMic = CreateCard(414, 228, 384, 206);
        Controls.Add(cardMic);
        cardMic.Controls.Add(CreateSectionHeader("Голос со смартфона (Микрофон ПК)", 16, 10));

        _lblMicStatus = new Label
        {
            Location = new Point(16, 36),
            Size = new Size(352, 58),
            ForeColor = Color.FromArgb(205, 214, 230)
        };
        cardMic.Controls.Add(_lblMicStatus);

        _btnInstallVbCable = CreateButton("Установить виртуальный микрофон (VB-Cable)", 16, 98, 352, 34, Color.FromArgb(30, 134, 88));
        _btnInstallVbCable.Click += async (_, _) => await InstallVbCableAsync();
        cardMic.Controls.Add(_btnInstallVbCable);

        var btnRefreshAudio = CreateButton("Обновить аудиоустройства", 16, 138, 352, 28, Color.FromArgb(42, 50, 66));
        btnRefreshAudio.Click += (_, _) => _server.MicManager.RefreshDevices(recoverIfNeeded: false);
        cardMic.Controls.Add(btnRefreshAudio);

        _chkSpeakers = new CheckBox
        {
            Text = "Выводить голос в динамики ПК (вместо вирт. микрофона)",
            Location = new Point(16, 174),
            AutoSize = true,
            ForeColor = Color.FromArgb(215, 222, 236),
            Checked = _server.Config.OutputVoiceToSpeakers
        };
        _chkSpeakers.CheckedChanged += (_, _) => _server.MicManager.SetOutputToSpeakers(_chkSpeakers.Checked);
        cardMic.Controls.Add(_chkSpeakers);

        // Right Bottom Card: Notifications & Autostart
        var cardNotif = CreateCard(414, 442, 384, 96);
        Controls.Add(cardNotif);
        cardNotif.Controls.Add(CreateSectionHeader("Уведомления ПК → Смартфон", 16, 10));

        var btnTestNotif = CreateButton("Тест на телефон", 252, 8, 118, 26, Color.FromArgb(24, 122, 198));
        btnTestNotif.Click += async (_, _) => await _server.SendTestNotificationAsync();
        cardNotif.Controls.Add(btnTestNotif);

        _chkForwardNotifs = new CheckBox
        {
            Text = "Пересылать уведомления Windows на смартфон",
            Location = new Point(16, 40),
            AutoSize = true,
            ForeColor = Color.FromArgb(215, 222, 236),
            Checked = _server.Config.ForwardPcNotifications
        };
        _chkForwardNotifs.CheckedChanged += (_, _) => _server.SetForwardNotifications(_chkForwardNotifs.Checked);
        cardNotif.Controls.Add(_chkForwardNotifs);

        _chkAutoStart = new CheckBox
        {
            Text = "Автозапуск вместе с Windows (сразу в трее)",
            Location = new Point(16, 66),
            AutoSize = true,
            ForeColor = Color.FromArgb(215, 222, 236),
            Checked = IsAutoStartEnabled()
        };
        _chkAutoStart.CheckedChanged += (_, _) => SetAutoStart(_chkAutoStart.Checked);
        cardNotif.Controls.Add(_chkAutoStart);

        // Bottom Log Box
        _txtLog = new TextBox
        {
            Location = new Point(22, 550),
            Size = new Size(776, 114),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(13, 15, 20),
            ForeColor = Color.FromArgb(148, 222, 180),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 8.5f)
        };
        Controls.Add(_txtLog);

        // System Tray
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Открыть RemotePC Host", null, (_, _) => ShowFromTray());
        trayMenu.Items.Add("Отправить тестовое уведомление", null, (_, _) => _ = _server.SendTestNotificationAsync());
        trayMenu.Items.Add("Копировать код привязки", null, (_, _) => Clipboard.SetText(_txtPairUri.Text));
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Выход", null, (_, _) =>
        {
            _reallyExit = true;
            Close();
        });

        _trayIcon = new NotifyIcon
        {
            Icon = _appIcon,
            Text = "RemotePC Host",
            Visible = true,
            ContextMenuStrip = trayMenu
        };
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();

        // Bind events
        _server.StateChanged += snap => SafeUi(() => ApplySnapshot(snap));
        _server.LogLine += line => SafeUi(() => AppendLog(line));
        _server.MicManager.StatusChanged += status => SafeUi(() => ApplyMicStatus(status));

        RefreshPairingQr();
        ApplySnapshot(_server.GetSnapshot());
        ApplyMicStatus(_server.MicManager.GetStatus());
        AppendLog($"RemotePC Host запущен. Экран: {ScreenStreamer.GetPrimaryScreenBounds().Width}x{ScreenStreamer.GetPrimaryScreenBounds().Height}");
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_startMinimized)
        {
            Hide();
            ShowInTaskbar = false;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            ShowInTaskbar = false;
            if (!_balloonShown)
            {
                _balloonShown = true;
                _trayIcon.ShowBalloonTip(2500, "RemotePC Host", "Программа свернута в системный трей и продолжает ждать смартфон.", ToolTipIcon.Info);
            }
            return;
        }

        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _appIcon.Dispose();
        base.OnFormClosing(e);
    }

    private void ShowFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void RefreshPairingQr(string? overrideUri = null)
    {
        string uri = overrideUri ?? _server.GetPairingUri();
        _txtPairUri.Text = uri;
        try
        {
            using var qrGen = new QRCodeGenerator();
            using var qrData = qrGen.CreateQrCode(uri, QRCodeGenerator.ECCLevel.M);
            using var qrCode = new QRCode(qrData);
            var old = _qrBox.Image;
            _qrBox.Image = qrCode.GetGraphic(8, Color.Black, Color.White, drawQuietZones: true);
            old?.Dispose();
        }
        catch { }
    }

    public void ApplyDemoStateForScreenshot()
    {
        string demoUri = "remotepc://pair?v=1&id=01020304demo5678&k=DEMO_KEY_NOT_VALID_000000000000000000000000&lan=192.168.1.10%3A8787&n=HOME-PC";
        RefreshPairingQr(demoUri);

        _lblCloudStatus.Text = "Облачный WSS (4G/5G): Активен (test.mosquitto.org)";
        _lblCloudStatus.ForeColor = Color.FromArgb(110, 235, 160);
        _lblLanStatus.Text = "Домашний Wi-Fi: ws://192.168.1.10:8787/ws";
        _lblLanStatus.ForeColor = Color.FromArgb(140, 210, 255);
        _lblClientStatus.Text = "Смартфон: Android 16 • Wi-Fi • 1080p Q75% • 30 FPS";
        _lblClientStatus.ForeColor = Color.FromArgb(110, 235, 160);
        _btnDisconnectClient.Visible = true;

        _lblMicStatus.Text = "Виртуальный микрофон: Готов (CABLE Input)\r\nОсновной микрофон ПК: USB Desktop Microphone\r\nРежим: авто-подмена только на время речи";
        _btnInstallVbCable.Text = "Переустановить виртуальный микрофон (VB-Cable)";

        _txtLog.Text =
            "[08:40:12] Облачный канал WSS подключён (wss://test.mosquitto.org:8081)\r\n" +
            "[08:40:14] Смартфон подключён: Android 16 (Wi-Fi LAN, 1080p Q75% @ 30 FPS)\r\n" +
            "[08:41:05] Уведомление -> смартфон [Telegram]: Новое сообщение\r\n" +
            "[08:41:28] Микрофон со смартфона: ВКЛЮЧЁН";

        _trayIcon.Visible = false;
    }

    public void DisposeForDemo()
    {
        _reallyExit = true;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _appIcon.Dispose();
        Dispose();
    }

    private void ApplySnapshot(TransportSnapshot s)
    {
        _lblCloudStatus.Text = s.CloudReady
            ? $"Облачный WSS (4G/5G): Активен ({new Uri(s.CloudBroker).Host})"
            : "Облачный WSS (4G/5G): Подключение...";
        _lblCloudStatus.ForeColor = s.CloudReady ? Color.FromArgb(110, 235, 160) : Color.FromArgb(245, 198, 96);

        _lblLanStatus.Text = $"Домашний Wi-Fi: ws://{s.LanIp}:{s.LanPort}/ws";
        _lblLanStatus.ForeColor = Color.FromArgb(140, 210, 255);

        if (s.ClientConnected)
        {
            string mode = s.ActiveTransport == ActiveTransportKind.LocalWifi ? "Домашний Wi-Fi" : "Мобильный интернет (WSS)";
            _lblClientStatus.Text = $"Смартфон: {s.ClientLabel} • {mode} • {s.VideoHeight}p Q{s.JpegQuality}% • {s.Fps}/{s.TargetFps} FPS";
            _lblClientStatus.ForeColor = Color.FromArgb(110, 235, 160);
            _btnDisconnectClient.Visible = true;
            _trayIcon.Text = $"RemotePC Host — {mode} ({s.VideoHeight}p, {s.Fps} FPS)";
        }
        else
        {
            _lblClientStatus.Text = "Смартфон: ожидание подключения";
            _lblClientStatus.ForeColor = Color.FromArgb(175, 184, 200);
            _btnDisconnectClient.Visible = false;
            _trayIcon.Text = "RemotePC Host — Ожидание";
        }
    }

    private void ApplyMicStatus(MicDeviceStatus st)
    {
        string cableText = st.HasVbCable
            ? $"Виртуальный микрофон: Готов ({st.CableCaptureName})"
            : "Виртуальный микрофон: Не установлен (нужен VB-Cable)";
        string baseText = $"Основной микрофон ПК: {st.BaselineMicName}";
        string stateText = st.IsSwitchedToVirtual
            ? "СЕЙЧАС АКТИВЕН ГОЛОС СО СМАРТФОНА"
            : (st.OutputToSpeakers ? "Режим: вывод голоса в динамики ПК" : "Режим: авто-подмена только на время речи");

        _lblMicStatus.Text = $"{cableText}\r\n{baseText}\r\n{stateText}";
        _btnInstallVbCable.Text = st.HasVbCable
            ? "Переустановить виртуальный микрофон (VB-Cable)"
            : "Установить виртуальный микрофон (VB-Cable)";
    }

    private async Task InstallVbCableAsync()
    {
        _btnInstallVbCable.Enabled = false;
        var prog = new Progress<string>(msg => AppendLog(msg));
        var (ok, message) = await _server.MicManager.DownloadAndInstallVbCableAsync(prog);
        AppendLog(message);
        MessageBox.Show(this, message, "Виртуальный микрофон VB-Cable",
            MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        _btnInstallVbCable.Enabled = true;
    }

    private async Task PairViaUsbAsync()
    {
        string? adb = FindAdbPath();
        if (adb == null)
        {
            MessageBox.Show(this, "adb.exe не найден. Отсканируйте QR-код камерой телефона в приложении RemotePC.",
                "USB-привязка", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string uri = _server.GetPairingUri();
        AppendLog("Отправка профиля привязки на подключённый по USB смартфон...");

        try
        {
            var psi = new ProcessStartInfo(adb,
                $"shell am start -n com.remotepc.app/.MainActivity --es pair_uri \"'{uri}'\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi)!;
            string stdout = await proc.StandardOutput.ReadToEndAsync();
            string stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode == 0 && !stderr.Contains("Error", StringComparison.OrdinalIgnoreCase))
            {
                AppendLog("Профиль привязки успешно передан на смартфон по USB!");
            }
            else
            {
                AppendLog("Ошибка USB-привязки: " + (string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim());
            }
        }
        catch (Exception ex)
        {
            AppendLog("Ошибка запуска ADB: " + ex.Message);
        }
    }

    private void ResetKeyAndRefresh()
    {
        var r = MessageBox.Show(this,
            "Создать новый 256-битный ключ? После сброса нужно будет заново привязать смартфон (QR-код или USB) и перезапустить RemotePC Host.",
            "Сброс ключа безопасности",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (r != DialogResult.Yes) return;

        _server.Config.ChannelId = "";
        _server.Config.KeyBase64Url = "";
        CryptoSession.SaveConfig(_server.Config);
        MessageBox.Show(this, "Ключ сброшен. Перезапустите RemotePC Host для активации нового ключа.", "Готово", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(RunValueName) != null;
        }
        catch { return false; }
    }

    private void SetAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                         ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (enable)
            {
                string exePath = Environment.ProcessPath ?? Application.ExecutablePath;
                key.SetValue(RunValueName, $"\"{exePath}\" --minimized");
                AppendLog("Автозапуск с Windows включён.");
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
                AppendLog("Автозапуск с Windows отключён.");
            }
            _server.Config.AutoStartWithWindows = enable;
            CryptoSession.SaveConfig(_server.Config);
        }
        catch (Exception ex)
        {
            AppendLog("Не удалось изменить автозапуск: " + ex.Message);
        }
    }

    private static string? FindAdbPath()
    {
        string localSdk = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Android", "Sdk", "platform-tools", "adb.exe");
        if (File.Exists(localSdk)) return localSdk;

        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            string cand = Path.Combine(dir.Trim(), "adb.exe");
            if (File.Exists(cand)) return cand;
        }
        return null;
    }

    private void AppendLog(string line)
    {
        if (_txtLog.IsDisposed) return;
        if (_txtLog.Lines.Length > 200)
        {
            _txtLog.Lines = _txtLog.Lines[^150..];
        }
        _txtLog.AppendText(line + Environment.NewLine);
    }

    private void SafeUi(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(action); } catch { }
        }
        else
        {
            action();
        }
    }

    private static Panel CreateCard(int x, int y, int w, int h) => new()
    {
        Location = new Point(x, y),
        Size = new Size(w, h),
        BackColor = Color.FromArgb(27, 31, 42)
    };

    private static Label CreateSectionHeader(string text, int x, int y) => new()
    {
        Text = text,
        Location = new Point(x, y),
        AutoSize = true,
        Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
        ForeColor = Color.FromArgb(230, 238, 252)
    };

    private static Label CreateStatusLabel(string text, int x, int y) => new()
    {
        Text = text,
        Location = new Point(x, y),
        AutoSize = true,
        ForeColor = Color.FromArgb(190, 200, 218)
    };

    private static Button CreateButton(string text, int x, int y, int w, int h, Color bg) => new()
    {
        Text = text,
        Location = new Point(x, y),
        Size = new Size(w, h),
        FlatStyle = FlatStyle.Flat,
        BackColor = bg,
        ForeColor = Color.White,
        Cursor = Cursors.Hand,
        FlatAppearance = { BorderSize = 0 }
    };

    private static Icon CreateAppIcon()
    {
        try
        {
            string? exePath = Environment.ProcessPath ?? Application.ExecutablePath;
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                Icon? embedded = Icon.ExtractAssociatedIcon(exePath);
                if (embedded != null) return embedded;
            }
        }
        catch { }

        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using var bg = new SolidBrush(Color.FromArgb(24, 122, 198));
        g.FillEllipse(bg, 1, 1, 30, 30);
        using var pen = new Pen(Color.White, 2.2f);
        g.DrawRectangle(pen, 6, 8, 14, 10);
        g.DrawLine(pen, 10, 21, 16, 21);
        using var phoneBrush = new SolidBrush(Color.FromArgb(120, 240, 190));
        g.FillRectangle(phoneBrush, 18, 12, 8, 13);
        return Icon.FromHandle(bmp.GetHicon());
    }
}
