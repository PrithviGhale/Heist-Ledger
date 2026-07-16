using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GTAEarningsTracker.Services;

public sealed class GtaAmountPreprocessingService
{
    private static readonly double[] Scales = [1.0, 1.5, 2.0];

    public IReadOnlyList<PreprocessedImage> CreateVariants(BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var converted = ConvertToBgra32(source);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var stride = checked(width * 4);
        var original = new byte[checked(stride * height)];
        converted.CopyPixels(original, stride, 0);

        var luminance = new byte[width * height];
        var saturation = new byte[width * height];
        for (var pixel = 0; pixel < luminance.Length; pixel++)
        {
            var offset = pixel * 4;
            var blue = original[offset];
            var green = original[offset + 1];
            var red = original[offset + 2];
            luminance[pixel] = ToLuminance(red, green, blue);
            saturation[pixel] = (byte)(Math.Max(red, Math.Max(green, blue)) -
                Math.Min(red, Math.Min(green, blue)));
        }

        var brightMask = new byte[luminance.Length];
        var lowSaturationMask = new byte[luminance.Length];
        var greenSuppressionMask = new byte[luminance.Length];
        for (var pixel = 0; pixel < luminance.Length; pixel++)
        {
            var offset = pixel * 4;
            var blue = original[offset];
            var green = original[offset + 1];
            var red = original[offset + 2];
            brightMask[pixel] = luminance[pixel] >= 142 ? (byte)255 : (byte)0;
            lowSaturationMask[pixel] = luminance[pixel] >= 112 && saturation[pixel] <= 92
                ? (byte)255
                : (byte)0;
            var greenDominance = green - ((red + blue) / 2);
            greenSuppressionMask[pixel] =
                (luminance[pixel] >= 105 && saturation[pixel] <= 105) ||
                (luminance[pixel] >= 145 && greenDominance < 48)
                    ? (byte)255
                    : (byte)0;
        }

        var adaptiveMask = CreateAdaptiveMask(luminance, width, height);
        var closedMask = Erode(Dilate(lowSaturationMask, width, height), width, height);
        var denoisedMask = RemoveIsolatedNoise(lowSaturationMask, width, height);
        var grayscale = CreateGrayPixels(luminance, invert: false);
        var contrast = CreateContrastPixels(luminance);
        var baseVariants = new List<(string Name, BitmapSource Image)>
        {
            ("Original amount crop", converted),
            ("Grayscale", CreateBitmap(width, height, grayscale)),
            ("Increased contrast", CreateBitmap(width, height, contrast)),
            ("Bright-text threshold", CreateBitmap(width, height, CreateGrayPixels(brightMask, false))),
            ("Low-saturation bright-text threshold", CreateBitmap(width, height, CreateGrayPixels(lowSaturationMask, false))),
            ("Green-background suppression", CreateBitmap(width, height, CreateGrayPixels(greenSuppressionMask, false))),
            ("Adaptive local threshold", CreateBitmap(width, height, CreateGrayPixels(adaptiveMask, false))),
            ("Inverted black text on white", CreateBitmap(width, height, CreateGrayPixels(lowSaturationMask, true))),
            ("Morphological close", CreateBitmap(width, height, CreateGrayPixels(closedMask, false))),
            ("Mild noise removal", CreateBitmap(width, height, CreateGrayPixels(denoisedMask, false)))
        };

        var variants = new List<PreprocessedImage>(baseVariants.Count * Scales.Length);
        foreach (var (name, image) in baseVariants)
        {
            foreach (var scale in Scales)
            {
                var scaled = scale == 1
                    ? image
                    : new TransformedBitmap(image, new ScaleTransform(scale, scale));
                if (scaled.CanFreeze)
                {
                    scaled.Freeze();
                }

                variants.Add(new PreprocessedImage(
                    $"{name} {scale:0.0}x",
                    EncodePng(scaled),
                    scale));
            }
        }

        return variants;
    }

    public static BitmapSource DecodePreview(PreprocessedImage image)
    {
        using var stream = new MemoryStream(image.PngBytes);
        var decoder = new PngBitmapDecoder(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    private static byte[] CreateAdaptiveMask(byte[] luminance, int width, int height)
    {
        var integralWidth = width + 1;
        var integral = new long[integralWidth * (height + 1)];
        for (var y = 0; y < height; y++)
        {
            long rowTotal = 0;
            for (var x = 0; x < width; x++)
            {
                rowTotal += luminance[(y * width) + x];
                integral[((y + 1) * integralWidth) + x + 1] =
                    integral[(y * integralWidth) + x + 1] + rowTotal;
            }
        }

        var result = new byte[luminance.Length];
        var radius = Math.Clamp(Math.Min(width, height) / 24, 6, 22);
        for (var y = 0; y < height; y++)
        {
            var top = Math.Max(0, y - radius);
            var bottom = Math.Min(height - 1, y + radius);
            for (var x = 0; x < width; x++)
            {
                var left = Math.Max(0, x - radius);
                var right = Math.Min(width - 1, x + radius);
                var area = (right - left + 1) * (bottom - top + 1);
                var sum = integral[((bottom + 1) * integralWidth) + right + 1] -
                    integral[(top * integralWidth) + right + 1] -
                    integral[((bottom + 1) * integralWidth) + left] +
                    integral[(top * integralWidth) + left];
                var localAverage = sum / (double)area;
                var value = luminance[(y * width) + x];
                result[(y * width) + x] = value >= localAverage + 14 && value >= 82
                    ? (byte)255
                    : (byte)0;
            }
        }

        return result;
    }

    private static byte[] Dilate(byte[] source, int width, int height)
    {
        var result = new byte[source.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                byte maximum = 0;
                for (var offsetY = -1; offsetY <= 1 && maximum == 0; offsetY++)
                {
                    var sampleY = Math.Clamp(y + offsetY, 0, height - 1);
                    for (var offsetX = -1; offsetX <= 1; offsetX++)
                    {
                        var sampleX = Math.Clamp(x + offsetX, 0, width - 1);
                        if (source[(sampleY * width) + sampleX] != 0)
                        {
                            maximum = 255;
                            break;
                        }
                    }
                }

                result[(y * width) + x] = maximum;
            }
        }

        return result;
    }

    private static byte[] Erode(byte[] source, int width, int height)
    {
        var result = new byte[source.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                byte minimum = 255;
                for (var offsetY = -1; offsetY <= 1 && minimum != 0; offsetY++)
                {
                    var sampleY = Math.Clamp(y + offsetY, 0, height - 1);
                    for (var offsetX = -1; offsetX <= 1; offsetX++)
                    {
                        var sampleX = Math.Clamp(x + offsetX, 0, width - 1);
                        if (source[(sampleY * width) + sampleX] == 0)
                        {
                            minimum = 0;
                            break;
                        }
                    }
                }

                result[(y * width) + x] = minimum;
            }
        }

        return result;
    }

    private static byte[] RemoveIsolatedNoise(byte[] source, int width, int height)
    {
        var result = (byte[])source.Clone();
        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var index = (y * width) + x;
                if (source[index] == 0)
                {
                    continue;
                }

                var brightNeighbors = 0;
                for (var offsetY = -1; offsetY <= 1; offsetY++)
                {
                    for (var offsetX = -1; offsetX <= 1; offsetX++)
                    {
                        if (source[((y + offsetY) * width) + x + offsetX] != 0)
                        {
                            brightNeighbors++;
                        }
                    }
                }

                if (brightNeighbors <= 2)
                {
                    result[index] = 0;
                }
            }
        }

        return result;
    }

    private static byte[] CreateGrayPixels(byte[] values, bool invert)
    {
        var pixels = new byte[values.Length * 4];
        for (var index = 0; index < values.Length; index++)
        {
            var value = invert ? (byte)(255 - values[index]) : values[index];
            var offset = index * 4;
            pixels[offset] = value;
            pixels[offset + 1] = value;
            pixels[offset + 2] = value;
            pixels[offset + 3] = 255;
        }

        return pixels;
    }

    private static byte[] CreateContrastPixels(byte[] luminance)
    {
        var adjusted = new byte[luminance.Length];
        for (var index = 0; index < luminance.Length; index++)
        {
            adjusted[index] = (byte)Math.Clamp(
                (int)Math.Round(((luminance[index] - 105) * 1.85) + 128),
                0,
                255);
        }

        return CreateGrayPixels(adjusted, invert: false);
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

    private static BitmapSource CreateBitmap(int width, int height, byte[] pixels)
    {
        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte ToLuminance(byte red, byte green, byte blue)
    {
        return (byte)Math.Clamp(
            (int)Math.Round((red * 0.299) + (green * 0.587) + (blue * 0.114)),
            0,
            255);
    }

    private static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
