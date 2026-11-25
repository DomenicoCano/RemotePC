using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using RemotePcHost.Security;

namespace RemotePcHost.Services;

public sealed record MicDeviceStatus(
    bool HasVbCable,
    string? CableRenderName,
    string? CableCaptureName,
    string? BaselineMicName,
    bool IsSwitchedToVirtual,
    bool OutputToSpeakers);

/// <summary>
/// Manages virtual microphone output (VB-Audio Virtual Cable or PC speakers),
/// automatic default Windows Capture endpoint switching via COM IPolicyConfig
/// (with guaranteed recovery to user's baseline physical desktop microphone),
/// 1-click official VB-Cable driver installation, and Windows microphone-usage alerts.
/// </summary>
public sealed class VirtualMicrophoneManager : IDisposable
{
    private const string MicConsentKeyPath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    private readonly HostConfig _config;
    private readonly object _audioLock = new();
    private WasapiOut? _wasapiOut;
    private BufferedWaveProvider? _waveBuffer;
    private bool _isStreaming;
    private bool _isSwitchedToVirtual;
    private string? _cableRenderId;
    private string? _cableCaptureId;
    private string? _cableRenderName;
    private string? _cableCaptureName;

    private CancellationTokenSource? _watchCts;
    private string _lastActiveAppsSignature = "";

    public volatile bool IsClientConnected;

    public event Action<bool, string[]>? PcAppMicUsageChanged;
    public event Action<MicDeviceStatus>? StatusChanged;

    public VirtualMicrophoneManager(HostConfig config)
    {
        _config = config;
        RefreshDevices(recoverIfNeeded: true);
    }

    public MicDeviceStatus GetStatus() => new(
        HasVbCable: _cableRenderId != null && _cableCaptureId != null,
        CableRenderName: _cableRenderName,
        CableCaptureName: _cableCaptureName,
        BaselineMicName: _config.BaselineMicDeviceName ?? "(не найден)",
        IsSwitchedToVirtual: _isSwitchedToVirtual,
        OutputToSpeakers: _config.OutputVoiceToSpeakers);

    public void RefreshDevices(bool recoverIfNeeded = false)
    {
        _cableRenderId = null;
        _cableCaptureId = null;
        _cableRenderName = null;
        _cableCaptureName = null;

        try
        {
            using var enumerator = new MMDeviceEnumerator();

            foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                string name = dev.FriendlyName;
                if (IsVbCableDevice(name))
                {
                    _cableRenderId = dev.ID;
                    _cableRenderName = name;
                    break;
                }
            }

            MMDevice? firstNonCableCapture = null;
            foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                string name = dev.FriendlyName;
                if (IsVbCableDevice(name))
                {
                    _cableCaptureId = dev.ID;
                    _cableCaptureName = name;
                }
                else if (firstNonCableCapture == null)
                {
                    firstNonCableCapture = dev;
                }
            }

            // Capture baseline real microphone (e.g., USB desktop microphone)
            MMDevice? defaultCap = null;
            try { defaultCap = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications); } catch { }
            if (defaultCap == null)
            {
                try { defaultCap = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia); } catch { }
            }

            if (defaultCap != null && !IsVbCableDevice(defaultCap.FriendlyName))
            {
                if (_config.BaselineMicDeviceId != defaultCap.ID || _config.BaselineMicDeviceName != defaultCap.FriendlyName)
                {
                    _config.BaselineMicDeviceId = defaultCap.ID;
                    _config.BaselineMicDeviceName = defaultCap.FriendlyName;
                    CryptoSession.SaveConfig(_config);
                }
            }
            else if (string.IsNullOrEmpty(_config.BaselineMicDeviceId) && firstNonCableCapture != null)
            {
                _config.BaselineMicDeviceId = firstNonCableCapture.ID;
                _config.BaselineMicDeviceName = firstNonCableCapture.FriendlyName;
                CryptoSession.SaveConfig(_config);
            }

            // Crash recovery: if Windows default mic was left on VB-Cable when host wasn't streaming, restore baseline!
            if (recoverIfNeeded && !_isStreaming && defaultCap != null && IsVbCableDevice(defaultCap.FriendlyName)
                && !string.IsNullOrEmpty(_config.BaselineMicDeviceId))
            {
                TrySetDefaultEndpoint(_config.BaselineMicDeviceId);
            }
        }
        catch
        {
            // Audio enumeration may fail if no audio service running
        }

        StatusChanged?.Invoke(GetStatus());
    }

    public void SetOutputToSpeakers(bool enabled)
    {
        if (_config.OutputVoiceToSpeakers == enabled) return;
        _config.OutputVoiceToSpeakers = enabled;
        CryptoSession.SaveConfig(_config);

        lock (_audioLock)
        {
            if (_isStreaming)
            {
                StopPlaybackLocked();
                StartPlaybackLocked();
            }
        }
        StatusChanged?.Invoke(GetStatus());
    }

    public void StartVoiceStream()
    {
        RefreshDevices(recoverIfNeeded: false);
        lock (_audioLock)
        {
            if (_isStreaming) return;
            _isStreaming = true;
            StartPlaybackLocked();
        }
        StatusChanged?.Invoke(GetStatus());
    }

    public void FeedAudioPacket(ReadOnlySpan<byte> payload)
    {
        byte[] pcm = ImaAdpcmCodec.DecodePacketToPcm16(payload);
        if (pcm.Length == 0) return;

        lock (_audioLock)
        {
            if (!_isStreaming)
            {
                _isStreaming = true;
                StartPlaybackLocked();
            }

            if (_waveBuffer != null)
            {
                // Keep jitter buffer tight (< 240 ms) so voice stays real-time even over LTE bursts
                if (_waveBuffer.BufferedDuration.TotalMilliseconds > 240)
                    _waveBuffer.ClearBuffer();
                _waveBuffer.AddSamples(pcm, 0, pcm.Length);
            }
        }
    }

    public void StopVoiceStream()
    {
        lock (_audioLock)
        {
            if (!_isStreaming && !_isSwitchedToVirtual) return;
            _isStreaming = false;
            StopPlaybackLocked();
        }
        StatusChanged?.Invoke(GetStatus());
    }

    private void StartPlaybackLocked()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            MMDevice? targetRender = null;

            if (!_config.OutputVoiceToSpeakers && _cableRenderId != null)
            {
                try { targetRender = enumerator.GetDevice(_cableRenderId); } catch { }
            }

            if (targetRender == null && _config.OutputVoiceToSpeakers)
            {
                try { targetRender = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); } catch { }
            }

            // If user hasn't installed VB-Cable yet and didn't explicitly pick speakers,
            // do NOT blast voice unexpectedly unless OutputVoiceToSpeakers is checked — wait:
            // if VB-Cable is installed, route to VB-Cable and switch Windows default mic!
            if (targetRender == null)
                return;

            var format = new WaveFormat(16000, 16, 1);
            _waveBuffer = new BufferedWaveProvider(format)
            {
                BufferDuration = TimeSpan.FromMilliseconds(500),
                DiscardOnBufferOverflow = true
            };

            _wasapiOut = new WasapiOut(targetRender, AudioClientShareMode.Shared, useEventSync: true, latency: 40);
            _wasapiOut.Init(_waveBuffer);
            _wasapiOut.Play();

            if (!_config.OutputVoiceToSpeakers && _config.AutoSwitchDefaultMic && _cableCaptureId != null)
            {
                if (TrySetDefaultEndpoint(_cableCaptureId))
                    _isSwitchedToVirtual = true;
            }
        }
        catch
        {
            StopPlaybackLocked();
        }
    }

    private void StopPlaybackLocked()
    {
        try { _wasapiOut?.Stop(); } catch { }
        try { _wasapiOut?.Dispose(); } catch { }
        _wasapiOut = null;
        _waveBuffer = null;

        if (_isSwitchedToVirtual)
        {
            _isSwitchedToVirtual = false;
            if (!string.IsNullOrEmpty(_config.BaselineMicDeviceId))
                TrySetDefaultEndpoint(_config.BaselineMicDeviceId);
        }
    }

    public void StartMicUsageWatcher()
    {
        if (_watchCts != null) return;
        _watchCts = new CancellationTokenSource();
        var ct = _watchCts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!IsClientConnected)
                    {
                        _lastActiveAppsSignature = "";
                        await Task.Delay(2500, ct).ConfigureAwait(false);
                        continue;
                    }

                    string[] activeApps = PollActiveMicApps();
                    string sig = string.Join("|", activeApps);
                    if (sig != _lastActiveAppsSignature)
                    {
                        _lastActiveAppsSignature = sig;
                        PcAppMicUsageChanged?.Invoke(activeApps.Length > 0, activeApps);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { }

                try { await Task.Delay(750, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }, ct);
    }

    public static string[] PollActiveMicApps()
    {
        var result = new List<string>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(MicConsentKeyPath);
            if (root == null) return Array.Empty<string>();

            ScanConsentSubkeys(root, result);
            using var nonPackaged = root.OpenSubKey("NonPackaged");
            if (nonPackaged != null)
                ScanConsentSubkeys(nonPackaged, result);
        }
        catch { }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToArray();
    }

    private static void ScanConsentSubkeys(RegistryKey parent, List<string> activeApps)
    {
        foreach (string subName in parent.GetSubKeyNames())
        {
            if (subName.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var sub = parent.OpenSubKey(subName);
                if (sub == null) continue;
                long start = Convert.ToInt64(sub.GetValue("LastUsedTimeStart") ?? 0L);
                long stop = Convert.ToInt64(sub.GetValue("LastUsedTimeStop") ?? 0L);
                if (start > 0 && stop == 0)
                {
                    string pretty = PrettifyAppName(subName);
                    if (!pretty.Contains("RemotePcHost", StringComparison.OrdinalIgnoreCase))
                        activeApps.Add(pretty);
                }
            }
            catch { }
        }
    }

    private static string PrettifyAppName(string regKeyName)
    {
        // NonPackaged keys look like: C:#Program Files#Telegram Desktop#Telegram.exe
        string cleaned = regKeyName.Replace('#', '\\');
        string leaf = Path.GetFileNameWithoutExtension(cleaned);
        if (string.IsNullOrWhiteSpace(leaf)) leaf = regKeyName;
        int underscore = leaf.IndexOf('_');
        if (underscore > 2 && leaf.Contains('.'))
            leaf = leaf.Substring(0, underscore);
        return leaf;
    }

    /// <summary>
    /// Downloads and launches the official Microsoft WHQL-signed VB-Audio Virtual Cable installer (requires UAC).
    /// </summary>
    public async Task<(bool Ok, string Message)> DownloadAndInstallVbCableAsync(IProgress<string>? progress = null)
    {
        string[] urls =
        {
            "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip",
            "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack43.zip"
        };

        string workDir = Path.Combine(Path.GetTempPath(), "RemotePC_VBCable_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workDir);
        string zipPath = Path.Combine(workDir, "vbcable.zip");

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RemotePcHost/1.0");

            bool downloaded = false;
            foreach (string url in urls)
            {
                try
                {
                    progress?.Report("Скачивание драйвера VB-Audio Virtual Cable...");
                    byte[] bytes = await http.GetByteArrayAsync(url).ConfigureAwait(false);
                    if (bytes.Length > 100_000)
                    {
                        await File.WriteAllBytesAsync(zipPath, bytes).ConfigureAwait(false);
                        downloaded = true;
                        break;
                    }
                }
                catch { }
            }

            if (!downloaded)
                return (false, "Не удалось скачать VB-Cable. Проверьте интернет или установите вручную с vb-audio.com/Cable.");

            progress?.Report("Распаковка драйвера...");
            ZipFile.ExtractToDirectory(zipPath, workDir, overwriteFiles: true);

            string? setupExe = Directory.GetFiles(workDir, "VBCABLE_Setup_x64.exe", SearchOption.AllDirectories).FirstOrDefault()
                            ?? Directory.GetFiles(workDir, "VBCABLE_Setup*.exe", SearchOption.AllDirectories).FirstOrDefault();

            if (setupExe == null)
                return (false, "В архиве не найден VBCABLE_Setup_x64.exe.");

            progress?.Report("Запуск установщика (подтвердите UAC и нажмите Install Driver)...");
            var psi = new ProcessStartInfo(setupExe)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(setupExe)!
            };
            using var proc = Process.Start(psi);
            if (proc != null)
                await proc.WaitForExitAsync().ConfigureAwait(false);

            await Task.Delay(1200).ConfigureAwait(false);
            RefreshDevices(recoverIfNeeded: false);

            return GetStatus().HasVbCable
                ? (true, "Виртуальный микрофон VB-Audio Virtual Cable установлен и готов!")
                : (false, "Установщик закрыт, но устройство VB-Cable ещё не появилось. Если установщик просил перезагрузку — перезагрузите ПК.");
        }
        catch (Exception ex)
        {
            return (false, "Ошибка установки VB-Cable: " + ex.Message);
        }
    }

    private static bool IsVbCableDevice(string friendlyName) =>
        friendlyName.Contains("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase) ||
        friendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase) ||
        friendlyName.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase);

    private static bool TrySetDefaultEndpoint(string deviceId)
    {
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            int hr1 = policy.SetDefaultEndpoint(deviceId, ERole.eConsole);
            int hr2 = policy.SetDefaultEndpoint(deviceId, ERole.eMultimedia);
            int hr3 = policy.SetDefaultEndpoint(deviceId, ERole.eCommunications);
            Marshal.ReleaseComObject(policy);
            return hr1 == 0 || hr2 == 0 || hr3 == 0;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _watchCts?.Cancel();
        _watchCts?.Dispose();
        StopVoiceStream();
    }

    private enum ERole
    {
        eConsole = 0,
        eMultimedia = 1,
        eCommunications = 2
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient { }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string pszDeviceName, IntPtr ppFormat);
        [PreserveSig] int GetDeviceFormat(string pszDeviceName, int bDefault, IntPtr ppFormat);
        [PreserveSig] int ResetDeviceFormat(string pszDeviceName);
        [PreserveSig] int SetDeviceFormat(string pszDeviceName, IntPtr pEndpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod(string pszDeviceName, int bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);
        [PreserveSig] int SetProcessingPeriod(string pszDeviceName, IntPtr pmftPeriod);
        [PreserveSig] int GetShareMode(string pszDeviceName, IntPtr pMode);
        [PreserveSig] int SetShareMode(string pszDeviceName, IntPtr mode);
        [PreserveSig] int GetPropertyValue(string pszDeviceName, IntPtr key, IntPtr pv);
        [PreserveSig] int SetPropertyValue(string pszDeviceName, IntPtr key, IntPtr pv);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, ERole role);
        [PreserveSig] int SetEndpointVisibility(string pszDeviceName, int bVisible);
    }
}
