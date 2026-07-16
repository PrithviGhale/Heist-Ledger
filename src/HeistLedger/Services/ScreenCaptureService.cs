using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using GTAEarningsTracker.Models;
using FormsScreen = System.Windows.Forms.Screen;

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
        if (!IsValidCaptureArea(settings))
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                "The capture area must be at least 100 by 40 pixels and remain inside the primary screen.");
        }

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            throw CreateCaptureException("Unable to access the screen device context.");
        }

        IntPtr memoryDc = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr previousObject = IntPtr.Zero;

        try
        {
            memoryDc = CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero)
            {
                throw CreateCaptureException("Unable to create the capture device context.");
            }

            bitmap = CreateCompatibleBitmap(screenDc, settings.Width, settings.Height);
            if (bitmap == IntPtr.Zero)
            {
                throw CreateCaptureException("Unable to create the capture bitmap.");
            }

            previousObject = SelectObject(memoryDc, bitmap);
            if (previousObject == IntPtr.Zero || previousObject == new IntPtr(-1))
            {
                throw CreateCaptureException("Unable to prepare the capture bitmap.");
            }

            if (!BitBlt(
                    memoryDc,
                    0,
                    0,
                    settings.Width,
                    settings.Height,
                    screenDc,
                    settings.X,
                    settings.Y,
                    SrcCopy | CaptureBlt))
            {
                throw CreateCaptureException("Windows could not copy the selected screen pixels.");
            }

            var bitmapSource = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            return excludedScreenRectangles is { Count: > 0 }
                ? BlackOutRegions(bitmapSource, settings, excludedScreenRectangles)
                : Freeze(bitmapSource);
        }
        finally
        {
            if (previousObject != IntPtr.Zero && previousObject != new IntPtr(-1) && memoryDc != IntPtr.Zero)
            {
                SelectObject(memoryDc, previousObject);
            }

            if (bitmap != IntPtr.Zero)
            {
                DeleteObject(bitmap);
            }

            if (memoryDc != IntPtr.Zero)
            {
                DeleteDC(memoryDc);
            }

            ReleaseDC(IntPtr.Zero, screenDc);
        }
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

    private static BitmapSource Freeze(BitmapSource source)
    {
        source.Freeze();
        return source;
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
    private static extern IntPtr CreateCompatibleBitmap(IntPtr deviceContext, int width, int height);

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
