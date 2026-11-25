using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RemotePcHost.Services;

public sealed record PcNotificationItem(
    long SqliteOrder,
    string App,
    string Title,
    string Body,
    long ArrivalUnixMs);

/// <summary>
/// Watches Windows 10/11 Action Center SQLite database (%LOCALAPPDATA%\Microsoft\Windows\Notifications\wpndatabase.db)
/// using built-in winsqlite3.dll on a temporary WAL-safe snapshot, with zero NuGet bloat and no MSIX requirement.
/// </summary>
public sealed class WindowsNotificationWatcher : IDisposable
{
    private const int SQLITE_OK = 0;
    private const int SQLITE_ROW = 100;
    private const int SQLITE_OPEN_READWRITE = 0x00000002;
    private const int SQLITE_OPEN_NOMUTEX = 0x00008000;

    private readonly string _wpnDir;
    private readonly string _wpnDbPath;
    private readonly string _snapDir;
    private readonly string _snapDbPath;
    private readonly SemaphoreSlim _pollLock = new(1, 1);

    private FileSystemWatcher? _fsw;
    private System.Threading.Timer? _fallbackTimer;
    private CancellationTokenSource? _cts;
    private int _pollScheduled;
    private long _lastOrder;

    public event Action<PcNotificationItem>? NotificationCaptured;

    public WindowsNotificationWatcher()
    {
        _wpnDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "Notifications");
        _wpnDbPath = Path.Combine(_wpnDir, "wpndatabase.db");
        _snapDir = Path.Combine(Path.GetTempPath(), $"RemotePcHost_wpn_{Environment.ProcessId}");
        _snapDbPath = Path.Combine(_snapDir, "wpndatabase.db");
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();

        if (TryReadCurrentMaxOrder(out long maxOrder, out _))
        {
            _lastOrder = maxOrder;
        }

        try
        {
            if (Directory.Exists(_wpnDir))
            {
                _fsw = new FileSystemWatcher(_wpnDir, "wpndatabase.db*")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true
                };
                _fsw.Changed += (_, _) => TriggerDebouncedPoll();
                _fsw.Created += (_, _) => TriggerDebouncedPoll();
            }
        }
        catch
        {
            // Fallback timer still runs even if FileSystemWatcher cannot attach
        }

        _fallbackTimer = new System.Threading.Timer(_ =>
        {
            if (_cts != null && !_cts.IsCancellationRequested)
                _ = PollNewNotificationsAsync();
        }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    private void TriggerDebouncedPoll()
    {
        if (_cts == null || _cts.IsCancellationRequested) return;
        if (Interlocked.Exchange(ref _pollScheduled, 1) == 1) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250, _cts.Token).ConfigureAwait(false);
                Interlocked.Exchange(ref _pollScheduled, 0);
                await PollNewNotificationsAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            finally
            {
                Interlocked.Exchange(ref _pollScheduled, 0);
            }
        });
    }

    private async Task PollNewNotificationsAsync()
    {
        if (_cts == null || _cts.IsCancellationRequested) return;
        if (!await _pollLock.WaitAsync(0).ConfigureAwait(false)) return;

        try
        {
            if (!CopySnapshotLocked()) return;

            var items = ReadNewNotificationsFromSnapshot(_snapDbPath, _lastOrder);
            foreach (var item in items)
            {
                if (item.SqliteOrder > _lastOrder)
                    _lastOrder = item.SqliteOrder;

                if (item.App.Contains("RemotePcHost", StringComparison.OrdinalIgnoreCase) ||
                    item.Title.Contains("RemotePC Host", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                NotificationCaptured?.Invoke(item);
            }
        }
        catch
        {
            // Ignore transient snapshot read errors while Windows commits WAL
        }
        finally
        {
            _pollLock.Release();
        }
    }

    public bool TryReadCurrentMaxOrder(out long maxOrder, out string detail)
    {
        maxOrder = 0;
        detail = "";
        try
        {
            if (!File.Exists(_wpnDbPath))
            {
                detail = "wpndatabase.db not found";
                return false;
            }
            if (!CopySnapshotLocked())
            {
                detail = "snapshot copy failed";
                return false;
            }

            if (!TryOpenSnapshot(_snapDbPath, out IntPtr db, out int rc))
            {
                detail = $"sqlite3_open_v2 rc={rc}";
                return false;
            }

            try
            {
                const string sql = "SELECT COALESCE(MAX([Order]), 0), COUNT(*) FROM Notification;";
                byte[] sqlBytes = Encoding.UTF8.GetBytes(sql);
                int prep = sqlite3_prepare_v2(db, sqlBytes, sqlBytes.Length, out IntPtr stmt, out _);
                if (prep != SQLITE_OK || stmt == IntPtr.Zero)
                {
                    detail = $"sqlite3_prepare_v2 rc={prep}";
                    return false;
                }

                try
                {
                    int step = sqlite3_step(stmt);
                    if (step == SQLITE_ROW)
                    {
                        maxOrder = sqlite3_column_int64(stmt, 0);
                        long count = sqlite3_column_int64(stmt, 1);
                        detail = $"rows={count}, maxOrder={maxOrder}";
                        return true;
                    }
                    detail = $"sqlite3_step rc={step}";
                    return false;
                }
                finally
                {
                    sqlite3_finalize(stmt);
                }
            }
            finally
            {
                sqlite3_close_v2(db);
            }
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
    }

    private bool CopySnapshotLocked()
    {
        if (!File.Exists(_wpnDbPath)) return false;
        Directory.CreateDirectory(_snapDir);

        CopyFileShared(_wpnDbPath, _snapDbPath);

        string srcWal = _wpnDbPath + "-wal";
        string dstWal = _snapDbPath + "-wal";
        if (File.Exists(srcWal))
            CopyFileShared(srcWal, dstWal);
        else if (File.Exists(dstWal))
            File.Delete(dstWal);

        string srcShm = _wpnDbPath + "-shm";
        string dstShm = _snapDbPath + "-shm";
        if (File.Exists(srcShm))
            CopyFileShared(srcShm, dstShm);
        else if (File.Exists(dstShm))
            File.Delete(dstShm);

        return true;
    }

    private static void CopyFileShared(string src, string dst)
    {
        using var inStream = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var outStream = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None);
        inStream.CopyTo(outStream);
    }

    private static bool TryOpenSnapshot(string dbPath, out IntPtr db, out int rc)
    {
        byte[] pathUtf8 = Encoding.UTF8.GetBytes(dbPath + "\0");
        rc = sqlite3_open_v2(pathUtf8, out db, SQLITE_OPEN_READWRITE | SQLITE_OPEN_NOMUTEX, IntPtr.Zero);
        if (rc != SQLITE_OK)
        {
            if (db != IntPtr.Zero)
            {
                sqlite3_close_v2(db);
                db = IntPtr.Zero;
            }
            return false;
        }
        return db != IntPtr.Zero;
    }

    private static List<PcNotificationItem> ReadNewNotificationsFromSnapshot(string dbPath, long afterOrder)
    {
        var list = new List<PcNotificationItem>();
        if (!TryOpenSnapshot(dbPath, out IntPtr db, out _))
            return list;

        try
        {
            const string sql = @"
                SELECT n.[Order], n.ArrivalTime, COALESCE(h.PrimaryId, ''), n.Payload
                FROM Notification n
                LEFT JOIN NotificationHandler h ON n.HandlerId = h.RecordId
                WHERE n.[Order] > ? AND LOWER(COALESCE(n.Type, '')) = 'toast'
                ORDER BY n.[Order] ASC
                LIMIT 25;";

            byte[] sqlBytes = Encoding.UTF8.GetBytes(sql);
            if (sqlite3_prepare_v2(db, sqlBytes, sqlBytes.Length, out IntPtr stmt, out _) != SQLITE_OK || stmt == IntPtr.Zero)
                return list;

            try
            {
                sqlite3_bind_int64(stmt, 1, afterOrder);
                while (sqlite3_step(stmt) == SQLITE_ROW)
                {
                    long order = sqlite3_column_int64(stmt, 0);
                    long fileTime = sqlite3_column_int64(stmt, 1);
                    IntPtr appPtr = sqlite3_column_text(stmt, 2);
                    string primaryId = appPtr != IntPtr.Zero ? (Marshal.PtrToStringUTF8(appPtr) ?? "") : "";

                    IntPtr blobPtr = sqlite3_column_blob(stmt, 3);
                    int blobLen = sqlite3_column_bytes(stmt, 3);
                    byte[] payload = Array.Empty<byte>();
                    if (blobPtr != IntPtr.Zero && blobLen > 0)
                    {
                        payload = new byte[blobLen];
                        Marshal.Copy(blobPtr, payload, 0, blobLen);
                    }

                    string app = FormatAppName(primaryId);
                    var (title, body) = ParseToastXml(payload);
                    if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body))
                        continue;
                    if (string.IsNullOrWhiteSpace(title))
                        title = app;

                    long tsMs = FileTimeToUnixMs(fileTime);
                    list.Add(new PcNotificationItem(order, app, title, body, tsMs));
                }
            }
            finally
            {
                sqlite3_finalize(stmt);
            }
        }
        finally
        {
            sqlite3_close_v2(db);
        }

        return list;
    }

    public static (string Title, string Body) ParseToastXml(ReadOnlySpan<byte> utf8Payload)
    {
        if (utf8Payload.IsEmpty) return ("", "");

        string raw = Encoding.UTF8.GetString(utf8Payload).Trim('\0', '\uFEFF', ' ', '\r', '\n', '\t');
        if (string.IsNullOrWhiteSpace(raw)) return ("", "");

        var lines = new List<string>();
        try
        {
            var doc = XDocument.Parse(raw, LoadOptions.None);
            foreach (var el in doc.Descendants())
            {
                if (!el.Name.LocalName.Equals("text", StringComparison.OrdinalIgnoreCase))
                    continue;

                string? placement = el.Attribute("placement")?.Value;
                if (string.Equals(placement, "attribution", StringComparison.OrdinalIgnoreCase))
                    continue;

                string val = el.Value.Trim();
                if (!string.IsNullOrWhiteSpace(val))
                    lines.Add(val);
            }
        }
        catch
        {
            // Regex fallback if toast XML contains unescaped characters
            foreach (Match m in Regex.Matches(raw, @"<text\b[^>]*>(.*?)</text>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                string val = System.Net.WebUtility.HtmlDecode(Regex.Replace(m.Groups[1].Value, "<.*?>", "")).Trim();
                if (!string.IsNullOrWhiteSpace(val))
                    lines.Add(val);
            }
        }

        if (lines.Count == 0) return ("", "");
        if (lines.Count == 1) return (lines[0], "");

        string title = lines[0];
        string body = string.Join("\n", lines.Skip(1).Where(l => !string.IsNullOrWhiteSpace(l)));
        return (title, body);
    }

    public static string FormatAppName(string primaryId)
    {
        if (string.IsNullOrWhiteSpace(primaryId)) return "Windows";
        string s = primaryId.Trim();

        if (s.Contains("Telegram", StringComparison.OrdinalIgnoreCase)) return "Telegram";
        if (s.Contains("Discord", StringComparison.OrdinalIgnoreCase)) return "Discord";
        if (s.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase)) return "WhatsApp";
        if (s.Contains("Viber", StringComparison.OrdinalIgnoreCase)) return "Viber";
        if (s.Contains("Slack", StringComparison.OrdinalIgnoreCase)) return "Slack";
        if (s.Contains("Teams", StringComparison.OrdinalIgnoreCase)) return "Microsoft Teams";
        if (s.Contains("MSEdge", StringComparison.OrdinalIgnoreCase) || s.Contains("MicrosoftEdge", StringComparison.OrdinalIgnoreCase))
            return "Microsoft Edge";
        if (s.Contains("Chrome", StringComparison.OrdinalIgnoreCase)) return "Google Chrome";
        if (s.Contains("Firefox", StringComparison.OrdinalIgnoreCase)) return "Firefox";
        if (s.Contains("Opera", StringComparison.OrdinalIgnoreCase)) return "Opera";
        if (s.Contains("Yandex", StringComparison.OrdinalIgnoreCase)) return "Yandex";
        if (s.Contains("Steam", StringComparison.OrdinalIgnoreCase)) return "Steam";
        if (s.Contains("Thunderbird", StringComparison.OrdinalIgnoreCase)) return "Thunderbird";
        if (s.Contains("Outlook", StringComparison.OrdinalIgnoreCase)) return "Outlook";
        if (s.Contains("Windows.SystemToast", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("Explorer", StringComparison.OrdinalIgnoreCase))
            return "Windows";

        int bang = s.IndexOf('!');
        if (bang > 0) s = s[..bang];
        int under = s.IndexOf('_');
        if (under > 0) s = s[..under];

        if (s.StartsWith("com.squirrel.", StringComparison.OrdinalIgnoreCase))
            s = s["com.squirrel.".Length..];
        if (s.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase))
            s = s["Microsoft.".Length..];

        int lastDot = s.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < s.Length - 1)
            s = s[(lastDot + 1)..];

        return string.IsNullOrWhiteSpace(s) ? "Windows" : s;
    }

    private static long FileTimeToUnixMs(long fileTime)
    {
        try
        {
            if (fileTime > 116444736000000000L)
                return new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime)).ToUnixTimeMilliseconds();
        }
        catch { }
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _fsw?.Dispose(); } catch { }
        try { _fallbackTimer?.Dispose(); } catch { }
        _cts?.Dispose();
        _pollLock.Dispose();
        try
        {
            if (Directory.Exists(_snapDir))
                Directory.Delete(_snapDir, recursive: true);
        }
        catch { }
    }

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    private static extern int sqlite3_open_v2(byte[] filename, out IntPtr ppDb, int flags, IntPtr zVfs);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    private static extern int sqlite3_close_v2(IntPtr db);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    private static extern int sqlite3_prepare_v2(IntPtr db, byte[] zSql, int nByte, out IntPtr ppStmt, out IntPtr pzTail);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    private static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    private static extern int sqlite3_step(IntPtr stmt);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    private static extern long sqlite3_column_int64(IntPtr stmt, int iCol);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    private static extern IntPtr sqlite3_column_text(IntPtr stmt, int iCol);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    private static extern IntPtr sqlite3_column_blob(IntPtr stmt, int iCol);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    private static extern int sqlite3_column_bytes(IntPtr stmt, int iCol);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    private static extern int sqlite3_finalize(IntPtr stmt);
}
