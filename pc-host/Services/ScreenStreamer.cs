using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RemotePcHost.Services;

public sealed record VideoStreamSettings(int MaxWidth, int MaxHeight, int JpegQuality, int TargetFps)
{
    public string Summary => $"{MaxHeight}p • Q{JpegQuality}% • {TargetFps} FPS";
}

public sealed class CapturedFrame
{
    public uint FrameId { get; init; }
    public uint BaseFrameId { get; init; }
    public bool IsDeltaTile { get; init; }
    public int ScreenWidth { get; init; }
    public int ScreenHeight { get; init; }
    public int EncodedWidth { get; init; }
    public int EncodedHeight { get; init; }
    public ushort TileX { get; init; }
    public ushort TileY { get; init; }
    public ushort TileW { get; init; }
    public ushort TileH { get; init; }
    public ushort CursorNormX { get; init; }
    public ushort CursorNormY { get; init; }
    public bool IsIBeamCursor { get; init; }
    public byte[] JpegBytes { get; init; } = Array.Empty<byte>();
    public bool ContentChanged { get; init; }
}

public sealed class ScreenStreamer : IDisposable
{
    private const int MaxAtomicTilePacketBytes = 27 * 1024;
    private const int TileHeaderSize = 29;

    public static readonly IReadOnlyDictionary<string, VideoStreamSettings> Profiles =
        new Dictionary<string, VideoStreamSettings>(StringComparer.OrdinalIgnoreCase)
        {
            ["eco"] = new(854, 480, 35, 10),
            ["balanced"] = new(1280, 720, 55, 15),
            ["sharp"] = new(1920, 1080, 75, 24),
            ["native"] = new(2560, 1440, 90, 60),
            ["ultra"] = new(2560, 1440, 90, 60)
        };

    private static readonly ImageCodecInfo JpegCodec = ImageCodecInfo
        .GetImageEncoders()
        .First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    private readonly IntPtr _ibeamCursor = LoadCursor(IntPtr.Zero, IDC_IBEAM);
    private readonly object _captureLock = new();

    private Bitmap? _captureBmp;
    private Bitmap? _scaleBmp;
    private Bitmap? _tileBmp;
    private byte[]? _prevPixels;
    private byte[]? _currPixels;

    private uint _frameCounter;
    private uint _lastKeyframeId;
    private int _deltasSinceKeyframe;
    private int _unchangedFrames;
    private int _monitorIndex;

    public VideoStreamSettings CurrentSettings { get; private set; } = Profiles["balanced"];
    public int MonitorIndex => _monitorIndex;
    public int MonitorCount => Math.Max(1, Screen.AllScreens.Length);

    public void SetProfile(string profileId)
    {
        if (Profiles.TryGetValue(profileId, out var p))
        {
            CurrentSettings = p;
            ForceNextFrame();
        }
    }

    public void SetVideoConfig(int maxHeight, int jpegQuality, int targetFps)
    {
        int h = Math.Clamp(maxHeight, 360, 2160);
        int w = (int)Math.Round(h * 16.0 / 9.0);
        int q = Math.Clamp(jpegQuality, 15, 95);
        int fps = Math.Clamp(targetFps, 1, 60);

        CurrentSettings = new VideoStreamSettings(w, h, q, fps);
        ForceNextFrame();
    }

    public void SetMonitor(int index)
    {
        int count = MonitorCount;
        _monitorIndex = Math.Clamp(index, 0, count - 1);
        ForceNextFrame();
    }

    public void ForceNextFrame()
    {
        lock (_captureLock)
        {
            _lastKeyframeId = 0;
            _deltasSinceKeyframe = 0;
            _unchangedFrames = 0;
        }
    }

    public Rectangle GetActiveScreenBounds()
    {
        var screens = Screen.AllScreens;
        if (screens.Length > 0)
        {
            int idx = Math.Clamp(_monitorIndex, 0, screens.Length - 1);
            return screens[idx].Bounds;
        }
        var primary = Screen.PrimaryScreen;
        if (primary != null) return primary.Bounds;
        return new Rectangle(0, 0, Math.Max(800, GetSystemMetrics(0)), Math.Max(600, GetSystemMetrics(1)));
    }

    public static Rectangle GetPrimaryScreenBounds()
    {
        var scr = Screen.PrimaryScreen;
        if (scr != null) return scr.Bounds;
        return new Rectangle(0, 0, Math.Max(800, GetSystemMetrics(0)), Math.Max(600, GetSystemMetrics(1)));
    }

    public CapturedFrame Capture(bool forceSend = false)
    {
        lock (_captureLock)
        {
            Rectangle bounds = GetActiveScreenBounds();
            int screenW = bounds.Width;
            int screenH = bounds.Height;

            var settings = CurrentSettings;
            int encW = Math.Min(settings.MaxWidth, screenW);
            int encH = (int)Math.Round(encW * (double)screenH / screenW);
            if (encH > settings.MaxHeight)
            {
                encH = settings.MaxHeight;
                encW = (int)Math.Round(encH * (double)screenW / screenH);
            }
            encW = Math.Max(320, encW & ~1);
            encH = Math.Max(180, encH & ~1);

            if (_captureBmp == null || _captureBmp.Width != screenW || _captureBmp.Height != screenH)
            {
                _captureBmp?.Dispose();
                _captureBmp = new Bitmap(screenW, screenH, PixelFormat.Format32bppRgb);
                _lastKeyframeId = 0;
            }

            bool isIBeam = false;
            ushort normCursorX = 32768;
            ushort normCursorY = 32768;
            bool capturedOk = false;

            using (var gFull = Graphics.FromImage(_captureBmp))
            {
                IntPtr dstHdc = gFull.GetHdc();
                IntPtr srcHdc = GetDC(IntPtr.Zero);
                try
                {
                    capturedOk = BitBlt(dstHdc, 0, 0, screenW, screenH, srcHdc, bounds.X, bounds.Y, SRCCOPY | CAPTUREBLT);
                    if (!capturedOk)
                    {
                        capturedOk = BitBlt(dstHdc, 0, 0, screenW, screenH, srcHdc, bounds.X, bounds.Y, SRCCOPY);
                    }

                    if (capturedOk)
                    {
                        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
                        if (GetCursorInfo(ref ci) && (ci.flags & CURSOR_SHOWING) != 0)
                        {
                            isIBeam = ci.hCursor == _ibeamCursor;
                            int relX = ci.ptScreenPos.X - bounds.X;
                            int relY = ci.ptScreenPos.Y - bounds.Y;
                            normCursorX = (ushort)Math.Clamp((int)Math.Round(relX * 65535.0 / Math.Max(1, screenW - 1)), 0, 65535);
                            normCursorY = (ushort)Math.Clamp((int)Math.Round(relY * 65535.0 / Math.Max(1, screenH - 1)), 0, 65535);

                            int drawX = relX;
                            int drawY = relY;
                            if (GetIconInfo(ci.hCursor, out ICONINFO iconInfo))
                            {
                                drawX -= iconInfo.xHotspot;
                                drawY -= iconInfo.yHotspot;
                                if (iconInfo.hbmMask != IntPtr.Zero) DeleteObject(iconInfo.hbmMask);
                                if (iconInfo.hbmColor != IntPtr.Zero) DeleteObject(iconInfo.hbmColor);
                            }
                            DrawIconEx(dstHdc, drawX, drawY, ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
                        }
                    }
                }
                finally
                {
                    ReleaseDC(IntPtr.Zero, srcHdc);
                    gFull.ReleaseHdc(dstHdc);
                }

                if (!capturedOk)
                {
                    gFull.Clear(Color.FromArgb(18, 22, 32));
                    using var font = new Font("Segoe UI", 28f, FontStyle.Bold);
                    using var brush = new SolidBrush(Color.FromArgb(148, 197, 255));
                    using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    gFull.DrawString("Экран защищён (UAC / Блокировка Windows)", font, brush,
                        new RectangleF(0, 0, screenW, screenH), fmt);
                }
            }

            Bitmap encodeBmp = _captureBmp;
            if (encW != screenW || encH != screenH)
            {
                if (_scaleBmp == null || _scaleBmp.Width != encW || _scaleBmp.Height != encH)
                {
                    _scaleBmp?.Dispose();
                    _scaleBmp = new Bitmap(encW, encH, PixelFormat.Format32bppRgb);
                    _lastKeyframeId = 0;
                }
                using var gScale = Graphics.FromImage(_scaleBmp);
                gScale.InterpolationMode = InterpolationMode.Bilinear;
                gScale.CompositingQuality = CompositingQuality.HighSpeed;
                gScale.SmoothingMode = SmoothingMode.None;
                gScale.DrawImage(_captureBmp, 0, 0, encW, encH);
                encodeBmp = _scaleBmp;
            }

            return EncodeBitmapLocked(encodeBmp, screenW, screenH, normCursorX, normCursorY, isIBeam, forceSend);
        }
    }

    internal CapturedFrame EncodeBitmapForTest(Bitmap bmp, bool forceKey = false)
    {
        lock (_captureLock)
        {
            return EncodeBitmapLocked(bmp, bmp.Width, bmp.Height, 32768, 32768, false, forceKey);
        }
    }

    private CapturedFrame EncodeBitmapLocked(
        Bitmap encodeBmp,
        int screenW,
        int screenH,
        ushort cursorNormX,
        ushort cursorNormY,
        bool isIBeam,
        bool forceKey)
    {
        int encW = encodeBmp.Width;
        int encH = encodeBmp.Height;
        int strideBytes = encW * 4;
        int totalBytes = strideBytes * encH;

        if (_currPixels == null || _currPixels.Length != totalBytes)
            _currPixels = new byte[totalBytes];

        CopyBitmapPixels(encodeBmp, _currPixels);

        var settings = CurrentSettings;
        bool needKeyframe = forceKey
                         || _lastKeyframeId == 0
                         || _prevPixels == null
                         || _prevPixels.Length != totalBytes
                         || _deltasSinceKeyframe >= 24;

        if (!needKeyframe && _prevPixels != null)
        {
            var (hasDiff, minX, minY, maxX, maxY) = FindDirtyRect(_prevPixels, _currPixels, encW, encH);
            if (!hasDiff)
            {
                int staticThreshold = Math.Clamp(settings.TargetFps * 2, 4, 24);
                if (_unchangedFrames < staticThreshold)
                {
                    _unchangedFrames++;
                    return new CapturedFrame
                    {
                        FrameId = _frameCounter,
                        ScreenWidth = screenW,
                        ScreenHeight = screenH,
                        EncodedWidth = encW,
                        EncodedHeight = encH,
                        CursorNormX = cursorNormX,
                        CursorNormY = cursorNormY,
                        IsIBeamCursor = isIBeam,
                        ContentChanged = false
                    };
                }
                // Periodic keepalive keyframe after long static screen
                needKeyframe = true;
            }
            else
            {
                int dx1 = Math.Max(0, (minX - 12) & ~1);
                int dy1 = Math.Max(0, (minY - 12) & ~1);
                int dx2 = Math.Min(encW, (maxX + 13) & ~1);
                int dy2 = Math.Min(encH, (maxY + 13) & ~1);
                int dw = dx2 - dx1;
                int dh = dy2 - dy1;

                long dirtyArea = (long)dw * dh;
                long fullArea = (long)encW * encH;

                if (dw >= 16 && dh >= 16 && dirtyArea <= (fullArea * 55L / 100L))
                {
                    if (_tileBmp == null || _tileBmp.Width != dw || _tileBmp.Height != dh)
                    {
                        _tileBmp?.Dispose();
                        _tileBmp = new Bitmap(dw, dh, PixelFormat.Format32bppRgb);
                    }
                    using (var gTile = Graphics.FromImage(_tileBmp))
                    {
                        gTile.CompositingMode = CompositingMode.SourceCopy;
                        gTile.DrawImage(encodeBmp,
                            new Rectangle(0, 0, dw, dh),
                            new Rectangle(dx1, dy1, dw, dh),
                            GraphicsUnit.Pixel);
                    }

                    int tileQ = Math.Min(95, settings.JpegQuality + 6);
                    byte[] tileJpeg = EncodeJpeg(_tileBmp, tileQ);
                    if (tileJpeg.Length + TileHeaderSize <= MaxAtomicTilePacketBytes)
                    {
                        Buffer.BlockCopy(_currPixels, 0, _prevPixels, 0, totalBytes);
                        uint baseId = _frameCounter;
                        uint fid = ++_frameCounter;
                        _deltasSinceKeyframe++;
                        _unchangedFrames = 0;

                        return new CapturedFrame
                        {
                            FrameId = fid,
                            BaseFrameId = baseId,
                            IsDeltaTile = true,
                            ScreenWidth = screenW,
                            ScreenHeight = screenH,
                            EncodedWidth = encW,
                            EncodedHeight = encH,
                            TileX = (ushort)dx1,
                            TileY = (ushort)dy1,
                            TileW = (ushort)dw,
                            TileH = (ushort)dh,
                            CursorNormX = cursorNormX,
                            CursorNormY = cursorNormY,
                            IsIBeamCursor = isIBeam,
                            JpegBytes = tileJpeg,
                            ContentChanged = true
                        };
                    }
                }
            }
        }

        // Full keyframe fallback
        if (_prevPixels == null || _prevPixels.Length != totalBytes)
            _prevPixels = new byte[totalBytes];
        Buffer.BlockCopy(_currPixels, 0, _prevPixels, 0, totalBytes);

        byte[] fullJpeg = EncodeJpeg(encodeBmp, settings.JpegQuality);
        uint fullFid = ++_frameCounter;
        _lastKeyframeId = fullFid;
        _deltasSinceKeyframe = 0;
        _unchangedFrames = 0;

        return new CapturedFrame
        {
            FrameId = fullFid,
            BaseFrameId = 0,
            IsDeltaTile = false,
            ScreenWidth = screenW,
            ScreenHeight = screenH,
            EncodedWidth = encW,
            EncodedHeight = encH,
            CursorNormX = cursorNormX,
            CursorNormY = cursorNormY,
            IsIBeamCursor = isIBeam,
            JpegBytes = fullJpeg,
            ContentChanged = true
        };
    }

    private static byte[] EncodeJpeg(Bitmap bmp, int quality)
    {
        using var ms = new MemoryStream(65536);
        using var encParams = new EncoderParameters(1);
        encParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(quality, 15, 95));
        bmp.Save(ms, JpegCodec, encParams);
        return ms.ToArray();
    }

    private static void CopyBitmapPixels(Bitmap bmp, byte[] dest)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            int rowBytes = bmp.Width * 4;
            int stride = data.Stride;
            if (stride == rowBytes)
            {
                Marshal.Copy(data.Scan0, dest, 0, rowBytes * bmp.Height);
            }
            else
            {
                for (int y = 0; y < bmp.Height; y++)
                {
                    Marshal.Copy(data.Scan0 + y * stride, dest, y * rowBytes, rowBytes);
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private static (bool HasDiff, int MinX, int MinY, int MaxX, int MaxY) FindDirtyRect(
        byte[] prev, byte[] curr, int width, int height)
    {
        int minX = width;
        int minY = height;
        int maxX = -1;
        int maxY = -1;
        int rowBytes = width * 4;

        for (int y = 0; y < height; y += 2)
        {
            int rowOff = y * rowBytes;
            for (int x = 0; x < width; x += 4)
            {
                int i = rowOff + x * 4;
                int db = Math.Abs(curr[i] - prev[i]);
                int dg = Math.Abs(curr[i + 1] - prev[i + 1]);
                int dr = Math.Abs(curr[i + 2] - prev[i + 2]);
                if ((db | dg | dr) > 6)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }

        return (maxX >= 0, minX, minY, maxX, maxY);
    }

    public static byte[] SerializeFramePayload(CapturedFrame frame)
    {
        byte[] buf = new byte[17 + frame.JpegBytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), frame.FrameId);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4, 2), (ushort)frame.ScreenWidth);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(6, 2), (ushort)frame.ScreenHeight);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(8, 2), (ushort)frame.EncodedWidth);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(10, 2), (ushort)frame.EncodedHeight);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(12, 2), frame.CursorNormX);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(14, 2), frame.CursorNormY);
        buf[16] = frame.IsIBeamCursor ? (byte)1 : (byte)0;
        Buffer.BlockCopy(frame.JpegBytes, 0, buf, 17, frame.JpegBytes.Length);
        return buf;
    }

    /// <summary>
    /// 29-byte header + JPEG tile bytes. Matches Android HybridTransportClient byte-for-byte.
    /// </summary>
    public static byte[] SerializeTilePayload(CapturedFrame frame)
    {
        byte[] buf = new byte[TileHeaderSize + frame.JpegBytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), frame.FrameId);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), frame.BaseFrameId);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(8, 2), (ushort)frame.ScreenWidth);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(10, 2), (ushort)frame.ScreenHeight);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(12, 2), (ushort)frame.EncodedWidth);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(14, 2), (ushort)frame.EncodedHeight);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(16, 2), frame.TileX);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(18, 2), frame.TileY);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(20, 2), frame.TileW);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(22, 2), frame.TileH);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(24, 2), frame.CursorNormX);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(26, 2), frame.CursorNormY);
        buf[28] = frame.IsIBeamCursor ? (byte)1 : (byte)0;
        Buffer.BlockCopy(frame.JpegBytes, 0, buf, TileHeaderSize, frame.JpegBytes.Length);
        return buf;
    }

    public void Dispose()
    {
        lock (_captureLock)
        {
            _captureBmp?.Dispose();
            _captureBmp = null;
            _scaleBmp?.Dispose();
            _scaleBmp = null;
            _tileBmp?.Dispose();
            _tileBmp = null;
            _prevPixels = null;
            _currPixels = null;
        }
    }

    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;
    private const int CURSOR_SHOWING = 0x00000001;
    private const int DI_NORMAL = 0x0003;
    private const int IDC_IBEAM = 32513;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int w, int h, IntPtr hdcSrc, int xSrc, int ySrc, int rop);
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO pci);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon, int cxWidth, int cyWidth, int istepIfAniCur, IntPtr hbrFlickerFreeDraw, int diFlags);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
}
