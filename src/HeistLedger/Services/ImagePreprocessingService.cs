using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GTAEarningsTracker.Services;

public sealed record PreprocessedImage(string Method, byte[] PngBytes, double Scale = 1.0);

public sealed class ImagePreprocessingService
{
    private const int EnlargementFactor = 3;

    public IReadOnlyList<PreprocessedImage> CreateVariants(BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.PixelWidth <= 0 || source.PixelHeight <= 0)
        {
            throw new ArgumentException("The captured image is empty.", nameof(source));
        }

        var convertedSource = ConvertToBgra32(source);
        var enlarged = new TransformedBitmap(
            convertedSource,
            new ScaleTransform(EnlargementFactor, EnlargementFactor));
        enlarged.Freeze();

        var enlargedBgra = ConvertToBgra32(enlarged);
        var width = enlargedBgra.PixelWidth;
        var height = enlargedBgra.PixelHeight;
        var stride = checked(width * 4);
        var originalPixels = new byte[checked(stride * height)];
        enlargedBgra.CopyPixels(originalPixels, stride, 0);

        var grayscalePixels = (byte[])originalPixels.Clone();
        var highContrastPixels = (byte[])originalPixels.Clone();
        var brightGreenPixels = (byte[])originalPixels.Clone();

        for (var index = 0; index < originalPixels.Length; index += 4)
        {
            var blue = originalPixels[index];
            var green = originalPixels[index + 1];
            var red = originalPixels[index + 2];
            var luminance = (byte)Math.Clamp(
                (int)Math.Round((red * 0.299) + (green * 0.587) + (blue * 0.114)),
                0,
                255);

            SetPixel(grayscalePixels, index, luminance);

            var highContrast = luminance >= 155 ? (byte)255 : (byte)0;
            SetPixel(highContrastPixels, index, highContrast);

            var isBright = luminance >= 185;
            var isGreenText = green >= 90 && green >= red * 1.08 && green >= blue * 1.05;
            var brightGreenThreshold = isBright || isGreenText ? (byte)255 : (byte)0;
            SetPixel(brightGreenPixels, index, brightGreenThreshold);
        }

        return
        [
            new PreprocessedImage("Original enlarged 3x", EncodePng(enlargedBgra)),
            new PreprocessedImage("Grayscale enlarged 3x", EncodePng(CreateBitmap(width, height, grayscalePixels, stride))),
            new PreprocessedImage("High-contrast black and white", EncodePng(CreateBitmap(width, height, highContrastPixels, stride))),
            new PreprocessedImage("Bright/green text threshold", EncodePng(CreateBitmap(width, height, brightGreenPixels, stride)))
        ];
    }

    private static BitmapSource ConvertToBgra32(BitmapSource source)
    {
        if (source.Format == PixelFormats.Bgra32)
        {
            return source;
        }

        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    private static BitmapSource CreateBitmap(int width, int height, byte[] pixels, int stride)
    {
        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void SetPixel(byte[] pixels, int index, byte value)
    {
        pixels[index] = value;
        pixels[index + 1] = value;
        pixels[index + 2] = value;
        pixels[index + 3] = 255;
    }
}
