using System.Windows;
using System.Windows.Media.Imaging;

namespace GTAEarningsTracker.Services;

public static class FullScreenScanService
{
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
