using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GTAEarningsTracker.Models;
using FormsScreen = System.Windows.Forms.Screen;
using Point = System.Windows.Point;

namespace GTAEarningsTracker.Services;

public sealed record MonitorCaptureTarget(
    string Id,
    string DisplayName,
    CaptureSettings Bounds,
    bool IsPrimary);

public sealed class ScreenCaptureService
{
    public const int MinimumCaptureWidth = 100;
    public const int MinimumCaptureHeight = 40;

    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const int SrcCopy = 0x00CC0020;
    private const int CaptureBlt = 0x40000000;

    public static CaptureSettings CreateEnclosingPixelBounds(Point start, Point end, CaptureSettings screen)
    {
        if (!double.IsFinite(start.X) || !double.IsFinite(start.Y) ||
            !double.IsFinite(end.X) || !double.IsFinite(end.Y) || screen.Width <= 0 || screen.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(start), "Screen selection coordinates must be finite.");
        var screenRight = (double)screen.X + screen.Width;
        var screenBottom = (double)screen.Y + screen.Height;
        var left = checked((int)Math.Floor(Math.Clamp(Math.Min(start.X, end.X), screen.X, screenRight)));
        var top = checked((int)Math.Floor(Math.Clamp(Math.Min(start.Y, end.Y), screen.Y, screenBottom)));
        var right = checked((int)Math.Ceiling(Math.Clamp(Math.Max(start.X, end.X), screen.X, screenRight)));
        var bottom = checked((int)Math.Ceiling(Math.Clamp(Math.Max(start.Y, end.Y), screen.Y, screenBottom)));
        return new CaptureSettings { X = left, Y = top, Width = checked(right - left), Height = checked(bottom - top) };
    }

    public CaptureSettings GetPrimaryScreenBounds()
    {
        var primary = FormsScreen.PrimaryScreen;
        if (primary is not null)
        {
            return FromRectangle(primary.Bounds);
        }

        return new CaptureSettings
        {
            X = 0,
            Y = 0,
            Width = GetSystemMetrics(SmCxScreen),
            Height = GetSystemMetrics(SmCyScreen)
        };
    }

    public IReadOnlyList<MonitorCaptureTarget> GetMonitorTargets()
    {
        var screens = FormsScreen.AllScreens
            .OrderByDescending(screen => screen.Primary)
            .ThenBy(screen => screen.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return screens.Select((screen, index) => new MonitorCaptureTarget(
            $"Device:{screen.DeviceName}",
            $"Monitor {index + 1}{(screen.Primary ? " (Primary)" : string.Empty)}",
            FromRectangle(screen.Bounds),
            screen.Primary)).ToArray();
    }

    public IReadOnlyList<MonitorCaptureTarget> ResolveMonitorTargets(string? monitorId)
    {
        var targets = GetMonitorTargets();
        if (string.Equals(monitorId, "All", StringComparison.OrdinalIgnoreCase))
        {
            return targets;
        }

        var selected = string.Equals(monitorId, "Primary", StringComparison.OrdinalIgnoreCase)
            ? targets.FirstOrDefault(target => target.IsPrimary)
            : targets.FirstOrDefault(target =>
                string.Equals(target.Id, monitorId, StringComparison.OrdinalIgnoreCase));
        return selected is null
            ? targets.Where(target => target.IsPrimary).Take(1).ToArray()
            : [selected];
    }

    public bool IsValidCaptureArea(CaptureSettings settings)
    {
        if (settings.Width < MinimumCaptureWidth || settings.Height < MinimumCaptureHeight)
        {
            return false;
        }

        var screen = GetVirtualScreenBounds();
        var right = (long)settings.X + settings.Width;
        var bottom = (long)settings.Y + settings.Height;

        return settings.X >= screen.X &&
               settings.Y >= screen.Y &&
               right <= (long)screen.X + screen.Width &&
               bottom <= (long)screen.Y + screen.Height;
    }

    public CaptureSettings GetVirtualScreenBounds()
    {
        return new CaptureSettings
        {
            X = GetSystemMetrics(SmXVirtualScreen),
            Y = GetSystemMetrics(SmYVirtualScreen),
            Width = GetSystemMetrics(SmCxVirtualScreen),
            Height = GetSystemMetrics(SmCyVirtualScreen)
        };
    }

    public BitmapSource Capture(
        CaptureSettings settings,
        IReadOnlyList<Int32Rect>? excludedScreenRectangles = null)
    {
        using var session = OpenSession(settings);
        var frame = session.CaptureFrame();
        return excludedScreenRectangles is { Count: > 0 }
            ? BlackOutRegions(frame, settings, excludedScreenRectangles)
            : frame;
    }

    public CaptureSession OpenSession(CaptureSettings settings)
    {
        if (!IsValidCaptureArea(settings))
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                "The capture area must be at least 100 by 40 pixels and remain inside the connected screen bounds.");
        }

        return new CaptureSession(settings);
    }

    // A session owns one top-down DIB for the entire burst. Each returned WPF
    // bitmap copies its pixels, so subsequent frames cannot mutate the preview.
    // Create, use, and dispose the session on the same capture worker thread.
    public sealed class CaptureSession : IDisposable
    {
        private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
        private readonly int _x;
        private readonly int _y;
        private readonly int _width;
        private readonly int _height;
        private readonly int _stride;
        private readonly int _bufferSize;
        private IntPtr _screenDc;
        private IntPtr _memoryDc;
        private IntPtr _bitmap;
        private IntPtr _previousObject;
        private IntPtr _pixels;
        private bool _disposed;

        internal CaptureSession(CaptureSettings settings)
        {
            _x = settings.X;
            _y = settings.Y;
            _width = settings.Width;
            _height = settings.Height;
            _stride = checked(_width * 4);
            _bufferSize = checked(_stride * _height);
            try
            {
                _screenDc = GetDC(IntPtr.Zero);
                if (_screenDc == IntPtr.Zero) throw CreateCaptureException("Unable to access the screen device context.");
                _memoryDc = CreateCompatibleDC(_screenDc);
                if (_memoryDc == IntPtr.Zero) throw CreateCaptureException("Unable to create the capture device context.");
                var info = new BitmapInfo
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfo>(),
                    Width = _width,
                    Height = -_height,
                    Planes = 1,
                    BitCount = 32,
                    SizeImage = (uint)_bufferSize
                };
                _bitmap = CreateDIBSection(_screenDc, ref info, 0, out _pixels, IntPtr.Zero, 0);
                if (_bitmap == IntPtr.Zero || _pixels == IntPtr.Zero) throw CreateCaptureException("Unable to create the capture bitmap.");
                _previousObject = SelectObject(_memoryDc, _bitmap);
                if (_previousObject == IntPtr.Zero || _previousObject == new IntPtr(-1))
                    throw CreateCaptureException("Unable to prepare the capture bitmap.");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public BitmapSource CaptureFrame()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
                throw new InvalidOperationException("Use the capture session on its creating thread.");
            if (!BitBlt(
                    _memoryDc,
                    0,
                    0,
                    _width,
                    _height,
                    _screenDc,
                    _x,
                    _y,
                    SrcCopy | CaptureBlt))
            {
                throw CreateCaptureException("Windows could not copy the selected screen pixels.");
            }

            // Complete pending GDI writes before WPF copies the DIB's memory.
            if (!GdiFlush()) throw CreateCaptureException("Windows could not finish the capture.");
            var frame = BitmapSource.Create(_width, _height, 96, 96,
                PixelFormats.Bgr32, null, _pixels, _bufferSize, _stride);
            frame.Freeze();
            return frame;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_previousObject != IntPtr.Zero && _previousObject != new IntPtr(-1) && _memoryDc != IntPtr.Zero)
            {
                SelectObject(_memoryDc, _previousObject);
            }
            if (_bitmap != IntPtr.Zero) DeleteObject(_bitmap);
            if (_memoryDc != IntPtr.Zero) DeleteDC(_memoryDc);
            if (_screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, _screenDc);
            _bitmap = _memoryDc = _screenDc = _pixels = IntPtr.Zero;
        }
    }

    public static void WaitForDesktopComposition()
    {
        // Wait for the desktop compositor to remove our hidden overlay before
        // taking the first frame. This replaces an arbitrary 100 ms sleep.
        _ = DwmFlush();
    }

    public BitmapSource BlackOutRegions(
        BitmapSource source,
        CaptureSettings captureBounds,
        IReadOnlyList<Int32Rect> excludedScreenRectangles)
    {
        ArgumentNullException.ThrowIfNull(source);
        var converted = source.Format == System.Windows.Media.PixelFormats.Bgra32
            ? source
            : new System.Windows.Media.Imaging.FormatConvertedBitmap(
                source,
                System.Windows.Media.PixelFormats.Bgra32,
                null,
                0);
        var stride = checked(converted.PixelWidth * 4);
        var pixels = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);

        foreach (var screenRect in excludedScreenRectangles)
        {
            var left = Math.Clamp(screenRect.X - captureBounds.X, 0, converted.PixelWidth);
            var top = Math.Clamp(screenRect.Y - captureBounds.Y, 0, converted.PixelHeight);
            var right = Math.Clamp(screenRect.X + screenRect.Width - captureBounds.X, 0, converted.PixelWidth);
            var bottom = Math.Clamp(screenRect.Y + screenRect.Height - captureBounds.Y, 0, converted.PixelHeight);
            if (right <= left || bottom <= top)
            {
                continue;
            }

            for (var y = top; y < bottom; y++)
            {
                Array.Clear(pixels, (y * stride) + (left * 4), (right - left) * 4);
                for (var x = left; x < right; x++)
                {
                    pixels[(y * stride) + (x * 4) + 3] = 255;
                }
            }
        }

        var result = BitmapSource.Create(
            converted.PixelWidth,
            converted.PixelHeight,
            converted.DpiX,
            converted.DpiY,
            System.Windows.Media.PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        result.Freeze();
        return result;
    }

    private static CaptureSettings FromRectangle(System.Drawing.Rectangle bounds)
    {
        return new CaptureSettings
        {
            X = bounds.X,
            Y = bounds.Y,
            Width = bounds.Width,
            Height = bounds.Height
        };
    }

    private static Win32Exception CreateCaptureException(string message)
    {
        return new Win32Exception(Marshal.GetLastWin32Error(), message);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr windowHandle, IntPtr deviceContext);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr deviceContext, ref BitmapInfo info,
        uint usage, out IntPtr pixels, IntPtr section, uint offset);

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GdiFlush();

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr graphicsObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(
        IntPtr destinationDeviceContext,
        int destinationX,
        int destinationY,
        int width,
        int height,
        IntPtr sourceDeviceContext,
        int sourceX,
        int sourceY,
        int rasterOperation);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr graphicsObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr deviceContext);
}
