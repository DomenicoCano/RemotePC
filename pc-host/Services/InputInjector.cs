using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace RemotePcHost.Services;

public sealed record ImeHintInfo(bool IsTextInput, double CaretNormY);

/// <summary>
/// Injects mouse, scroll, Unicode text (layout-independent RU/EN), virtual keys, hotkeys,
/// and system power actions via Win32 SendInput. Also detects active text input fields
/// after clicks using IDC_IBEAM cursor, GetGUIThreadInfo caret, and focused control class.
/// </summary>
public sealed class InputInjector
{
    private readonly Func<Rectangle> _boundsProvider;
    private readonly IntPtr _ibeamCursor = LoadCursor(IntPtr.Zero, IDC_IBEAM);
    private readonly object _stateLock = new();
    private readonly HashSet<ushort> _heldKeys = new();
    private bool _leftDown;
    private bool _rightDown;
    private bool _middleDown;

    public InputInjector(Func<Rectangle>? boundsProvider = null)
    {
        _boundsProvider = boundsProvider ?? ScreenStreamer.GetPrimaryScreenBounds;
    }

    public int HeldInputCount
    {
        get
        {
            lock (_stateLock)
            {
                return _heldKeys.Count + (_leftDown ? 1 : 0) + (_rightDown ? 1 : 0) + (_middleDown ? 1 : 0);
            }
        }
    }

    public void MoveMouseRelative(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        if (GetCursorPos(out POINT pt))
        {
            Rectangle b = _boundsProvider();
            int nx = Math.Clamp(pt.X + dx, b.Left, b.Right - 1);
            int ny = Math.Clamp(pt.Y + dy, b.Top, b.Bottom - 1);
            SetCursorPos(nx, ny);
        }
    }

    public void MoveMouseAbsolute(double normX, double normY)
    {
        Rectangle b = _boundsProvider();
        int x = b.Left + (int)Math.Round(Math.Clamp(normX, 0.0, 1.0) * (b.Width - 1));
        int y = b.Top + (int)Math.Round(Math.Clamp(normY, 0.0, 1.0) * (b.Height - 1));
        SetCursorPos(x, y);
    }

    public void MouseButton(string button, string action, double? normX = null, double? normY = null)
    {
        if (normX.HasValue && normY.HasValue)
            MoveMouseAbsolute(normX.Value, normY.Value);

        string btnNorm = button.ToLowerInvariant();
        uint downFlag, upFlag;
        switch (btnNorm)
        {
            case "right":
                downFlag = MOUSEEVENTF_RIGHTDOWN;
                upFlag = MOUSEEVENTF_RIGHTUP;
                break;
            case "middle":
                downFlag = MOUSEEVENTF_MIDDLEDOWN;
                upFlag = MOUSEEVENTF_MIDDLEUP;
                break;
            default:
                downFlag = MOUSEEVENTF_LEFTDOWN;
                upFlag = MOUSEEVENTF_LEFTUP;
                break;
        }

        switch (action.ToLowerInvariant())
        {
            case "down":
                lock (_stateLock)
                {
                    if (btnNorm == "right") _rightDown = true;
                    else if (btnNorm == "middle") _middleDown = true;
                    else _leftDown = true;
                }
                SendMouseFlag(downFlag, 0);
                break;
            case "up":
                lock (_stateLock)
                {
                    if (btnNorm == "right") _rightDown = false;
                    else if (btnNorm == "middle") _middleDown = false;
                    else _leftDown = false;
                }
                SendMouseFlag(upFlag, 0);
                break;
            case "dblclick":
                SendMouseFlag(downFlag, 0);
                SendMouseFlag(upFlag, 0);
                Thread.Sleep(25);
                SendMouseFlag(downFlag, 0);
                SendMouseFlag(upFlag, 0);
                break;
            default: // click
                SendMouseFlag(downFlag, 0);
                SendMouseFlag(upFlag, 0);
                break;
        }
    }

    public void MouseWheel(int dy, int dx = 0)
    {
        if (dy != 0)
            SendMouseFlag(MOUSEEVENTF_WHEEL, (uint)dy);
        if (dx != 0)
            SendMouseFlag(MOUSEEVENTF_HWHEEL, (uint)dx);
    }

    /// <summary>
    /// Types any Russian, English, symbol, or multi-character string via KEYEVENTF_UNICODE,
    /// completely bypassing Windows active keyboard layout!
    /// </summary>
    public void TypeUnicodeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var inputs = new List<INPUT>(text.Length * 2);
        foreach (char ch in text)
        {
            if (ch == '\r') continue;
            if (ch == '\n')
            {
                inputs.Add(MakeKeyInput(VK_RETURN, 0, 0));
                inputs.Add(MakeKeyInput(VK_RETURN, 0, KEYEVENTF_KEYUP));
                continue;
            }
            if (ch == '\t')
            {
                inputs.Add(MakeKeyInput(VK_TAB, 0, 0));
                inputs.Add(MakeKeyInput(VK_TAB, 0, KEYEVENTF_KEYUP));
                continue;
            }

            inputs.Add(MakeKeyInput(0, ch, KEYEVENTF_UNICODE));
            inputs.Add(MakeKeyInput(0, ch, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
        }

        if (inputs.Count > 0)
        {
            var arr = inputs.ToArray();
            SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
        }
    }

    public void SendVirtualKey(ushort vk, string action)
    {
        uint ext = IsExtendedKey(vk) ? KEYEVENTF_EXTENDEDKEY : 0;
        switch (action.ToLowerInvariant())
        {
            case "down":
                lock (_stateLock) _heldKeys.Add(vk);
                SendSingleKey(vk, ext);
                break;
            case "up":
                lock (_stateLock) _heldKeys.Remove(vk);
                SendSingleKey(vk, ext | KEYEVENTF_KEYUP);
                break;
            default: // tap
                var seq = new[]
                {
                    MakeKeyInput(vk, 0, ext),
                    MakeKeyInput(vk, 0, ext | KEYEVENTF_KEYUP)
                };
                SendInput((uint)seq.Length, seq, Marshal.SizeOf<INPUT>());
                break;
        }
    }

    public void ReleaseAllHeldInputs()
    {
        bool left, right, middle;
        ushort[] keys;
        lock (_stateLock)
        {
            left = _leftDown;
            right = _rightDown;
            middle = _middleDown;
            _leftDown = false;
            _rightDown = false;
            _middleDown = false;
            keys = _heldKeys.ToArray();
            _heldKeys.Clear();
        }

        if (left) SendMouseFlag(MOUSEEVENTF_LEFTUP, 0);
        if (right) SendMouseFlag(MOUSEEVENTF_RIGHTUP, 0);
        if (middle) SendMouseFlag(MOUSEEVENTF_MIDDLEUP, 0);

        foreach (ushort vk in keys)
        {
            uint ext = IsExtendedKey(vk) ? KEYEVENTF_EXTENDEDKEY : 0;
            SendSingleKey(vk, ext | KEYEVENTF_KEYUP);
        }
    }

    public static void SetClipboardText(string text)
    {
        if (text == null) return;
        Exception? err = null;
        var t = new Thread(() =>
        {
            try
            {
                Clipboard.SetText(text, TextDataFormat.UnicodeText);
            }
            catch (Exception ex) { err = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join(1500);
        _ = err;
    }

    public static string GetClipboardText()
    {
        string result = "";
        var t = new Thread(() =>
        {
            try
            {
                if (Clipboard.ContainsText())
                    result = Clipboard.GetText(TextDataFormat.UnicodeText);
            }
            catch { }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join(1500);
        return result;
    }

    public void SendHotkey(ushort[] vks)
    {
        if (vks.Length == 0) return;

        var inputs = new List<INPUT>(vks.Length * 2);
        foreach (ushort vk in vks)
        {
            uint ext = IsExtendedKey(vk) ? KEYEVENTF_EXTENDEDKEY : 0;
            inputs.Add(MakeKeyInput(vk, 0, ext));
        }
        for (int i = vks.Length - 1; i >= 0; i--)
        {
            ushort vk = vks[i];
            uint ext = IsExtendedKey(vk) ? KEYEVENTF_EXTENDEDKEY : 0;
            inputs.Add(MakeKeyInput(vk, 0, ext | KEYEVENTF_KEYUP));
        }

        var arr = inputs.ToArray();
        SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
    }

    public void ExecuteSystemAction(string action)
    {
        switch (action.ToLowerInvariant())
        {
            case "vol_up": SendVirtualKey(0xAF, "tap"); break;
            case "vol_down": SendVirtualKey(0xAE, "tap"); break;
            case "vol_mute": SendVirtualKey(0xAD, "tap"); break;
            case "media_play": SendVirtualKey(0xB3, "tap"); break;
            case "media_next": SendVirtualKey(0xB0, "tap"); break;
            case "media_prev": SendVirtualKey(0xB1, "tap"); break;
            case "alt_tab": SendHotkey(new ushort[] { 0x12, 0x09 }); break;
            case "task_manager": SendHotkey(new ushort[] { 0x11, 0x10, 0x1B }); break;
            case "minimize_all": SendHotkey(new ushort[] { 0x5B, 0x44 }); break;
            case "lock": LockWorkStation(); break;
            case "sleep": SetSuspendState(false, false, false); break;
            case "shutdown":
                Process.Start(new ProcessStartInfo("shutdown", "/s /t 5")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                break;
        }
    }

    /// <summary>
    /// Inspects current cursor shape (IDC_IBEAM), GUI thread caret (hwndCaret / rcCaret),
    /// and focused control class to detect whether the user clicked into a text input box.
    /// </summary>
    public ImeHintInfo DetectTextInputState()
    {
        Rectangle screen = _boundsProvider();
        double caretNormY = 0.5;
        bool hasCaret = false;
        bool isIBeam = false;
        bool isTextClass = false;

        // 1. Check cursor shape (IDC_IBEAM)
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (GetCursorInfo(ref ci) && (ci.flags & 1) != 0)
        {
            isIBeam = ci.hCursor == _ibeamCursor;
            caretNormY = Math.Clamp((ci.ptScreenPos.Y - screen.Top) / (double)Math.Max(1, screen.Height), 0.05, 0.95);
        }

        // 2. Check active foreground GUI thread caret & focus window
        IntPtr fg = GetForegroundWindow();
        if (fg != IntPtr.Zero)
        {
            uint tid = GetWindowThreadProcessId(fg, out _);
            var gti = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            if (tid != 0 && GetGUIThreadInfo(tid, ref gti))
            {
                if (gti.hwndCaret != IntPtr.Zero && (gti.rcCaret.Bottom > gti.rcCaret.Top || gti.rcCaret.Right > gti.rcCaret.Left))
                {
                    hasCaret = true;
                    var pt = new POINT { X = gti.rcCaret.Left, Y = gti.rcCaret.Bottom };
                    if (ClientToScreen(gti.hwndCaret, ref pt))
                    {
                        caretNormY = Math.Clamp((pt.Y - screen.Top) / (double)Math.Max(1, screen.Height), 0.05, 0.95);
                    }
                }

                IntPtr focusHwnd = gti.hwndFocus != IntPtr.Zero ? gti.hwndFocus : fg;
                string cls = GetWindowClassName(focusHwnd);
                if (IsKnownTextClass(cls))
                    isTextClass = true;
            }
        }

        bool isText = hasCaret || isTextClass || isIBeam;
        return new ImeHintInfo(isText, caretNormY);
    }

    private static bool IsKnownTextClass(string cls)
    {
        if (string.IsNullOrEmpty(cls)) return false;
        return cls.Contains("Edit", StringComparison.OrdinalIgnoreCase)
            || cls.Contains("RichEdit", StringComparison.OrdinalIgnoreCase)
            || cls.Contains("Scintilla", StringComparison.OrdinalIgnoreCase)
            || cls.Contains("TextBox", StringComparison.OrdinalIgnoreCase)
            || cls.Contains("SearchBox", StringComparison.OrdinalIgnoreCase)
            || cls.Contains("ConsoleWindowClass", StringComparison.OrdinalIgnoreCase)
            || cls.Contains("WindowsTerminal", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetWindowClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(128);
        int n = GetClassName(hwnd, sb, sb.Capacity);
        return n > 0 ? sb.ToString() : "";
    }

    private static void SendMouseFlag(uint flag, uint mouseData)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = 0,
                    dy = 0,
                    mouseData = mouseData,
                    dwFlags = flag,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static void SendSingleKey(ushort vk, uint flags)
    {
        var arr = new[] { MakeKeyInput(vk, 0, flags) };
        SendInput(1, arr, Marshal.SizeOf<INPUT>());
    }

    private static INPUT MakeKeyInput(ushort vk, ushort scan, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = scan,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = IntPtr.Zero
            }
        }
    };

    private static bool IsExtendedKey(ushort vk) => vk is
        0x21 or 0x22 or 0x23 or 0x24 or // PgUp, PgDn, End, Home
        0x25 or 0x26 or 0x27 or 0x28 or // Arrows
        0x2D or 0x2E or                 // Insert, Delete
        0x5B or 0x5C or                 // LWin, RWin
        0xA3 or 0xA5 or                 // RCtrl, RAlt
        0xAD or 0xAE or 0xAF or 0xB0 or 0xB1 or 0xB3; // Media keys

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_HWHEEL = 0x1000;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const ushort VK_TAB = 0x09;
    private const ushort VK_RETURN = 0x0D;
    private const int IDC_IBEAM = 32513;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO pci);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] private static extern bool LockWorkStation();
    [DllImport("powrprof.dll", SetLastError = true)] private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
}
