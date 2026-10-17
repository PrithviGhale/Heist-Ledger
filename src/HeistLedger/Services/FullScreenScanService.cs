using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Media;

namespace GTAEarningsTracker.Services;

public static class FullScreenScanService
{
    public static Int32Rect? FindUniformViewport(BitmapSource frame, CancellationToken token)
    {
        var image = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var stride = image.PixelWidth * 4;
        var pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);
        var corners = new[] { 0, stride - 4, (image.PixelHeight - 1) * stride, pixels.Length - 4 };
        if (corners.Any(i => Enumerable.Range(0, 3).Any(c => Math.Abs(pixels[i + c] - pixels[c]) > 6))) return null;
        var left = image.PixelWidth;
        var right = -1;
        var top = image.PixelHeight;
        var bottom = -1;
        for (var y = 0; y < image.PixelHeight; y++)
        {
            token.ThrowIfCancellationRequested();
            for (var x = 0; x < image.PixelWidth; x++)
            {
                var i = y * stride + x * 4;
                if (Math.Abs(pixels[i] - pixels[0]) <= 6 && Math.Abs(pixels[i + 1] - pixels[1]) <= 6 && Math.Abs(pixels[i + 2] - pixels[2]) <= 6) continue;
                left = Math.Min(left, x); right = Math.Max(right, x);
                top = Math.Min(top, y); bottom = Math.Max(bottom, y);
            }
        }
        // Only remove a uniform surrounding border on ALL four sides. Keep
        // native game pixels intact instead of arbitrarily resizing or trimming
        // a result screen whose layout the existing reader already understands.
        return left >= 8 && top >= 8 && right < image.PixelWidth - 8 && bottom < image.PixelHeight - 8 && right - left >= 400 && bottom - top >= 200
            ? new Int32Rect(left, top, right - left + 1, bottom - top + 1) : null;
    }

    public static List<BitmapSource> CropFrames(IEnumerable<BitmapSource> frames, Int32Rect? region)
    {
        return frames.Select(frame =>
        {
            if (region is not { } bounds) return frame;
            var crop = new CroppedBitmap(frame, bounds);
            // Copy the pixels so pending confirmations do not retain five
            // full desktop bitmaps through CroppedBitmap.Source references.
            var stride = checked((crop.PixelWidth * crop.Format.BitsPerPixel + 7) / 8);
            var pixels = new byte[checked(stride * crop.PixelHeight)];
            crop.CopyPixels(pixels, stride, 0);
            var independent = BitmapSource.Create(crop.PixelWidth, crop.PixelHeight, crop.DpiX, crop.DpiY,
                crop.Format, crop.Palette, pixels, stride);
            independent.Freeze();
            return independent;
        }).ToList();
    }
}
