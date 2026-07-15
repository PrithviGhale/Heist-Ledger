using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GTAEarningsTracker.Models;

namespace GTAEarningsTracker.Services;

public sealed record PreprocessingBatch(
    IReadOnlyList<PreprocessedImage> Images,
    double DurationMilliseconds);

public sealed class FastGtaPreprocessingService
{
    public PreprocessedImage CreateLocalContrastVariant(BitmapSource source)
    {
        var data = Prepare(source);
        return Encode("Amount local contrast", data.Width, data.Height, CreateLocalContrast(data));
    }

    public PreprocessedImage CreateGreenSuppressionVariant(BitmapSource source)
    {
        var data = Prepare(source);
        return Encode("Amount green suppression inverted", data.Width, data.Height, CreateGreenSuppression(data, invert: true));
    }

    public PreprocessedImage CreateLowSaturationVariant(BitmapSource source)
    {
        var data = Prepare(source);
        return Encode("Fallback low-saturation text 0.55x", data.Width, data.Height, CreateLegacyTextMask(data, invert: false), 0.55);
    }

    public PreprocessedImage CreateClosedTextVariant(BitmapSource source)
    {
        var data = Prepare(source);
        var mask = MorphologicalClose(CreateLegacyTextMask(data, invert: false), data.Width, data.Height);
        return Encode("Fallback closed text 0.55x", data.Width, data.Height, mask, 0.55);
    }

    public PreprocessedImage CreateNeutralBrightnessVariant(BitmapSource source)
    {
        var data = Prepare(source);
        return Encode(
            "Fallback neutral brightness 80 0.55x",
            data.Width,
            data.Height,
            CreateNeutralBrightnessMask(data, 80),
            0.55);
    }

    public PreprocessingBatch CreateHeadingVariants(BitmapSource source)
    {
        var stopwatch = Stopwatch.StartNew();
        var scale = Math.Min(
            1d,
            Math.Min(1600d / source.PixelWidth, 900d / source.PixelHeight));
        BitmapSource searchSource = source;
        if (scale < 0.999)
        {
            searchSource = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            searchSource.Freeze();
        }

        var data = Prepare(searchSource);
        var variants = new[]
        {
            EncodeWithCoordinateScale("Heading grayscale contrast", data.Width, data.Height, CreateGlobalContrast(data), scale),
            EncodeWithCoordinateScale("Heading bright low-saturation mask", data.Width, data.Height, CreateLowSaturationMask(data, invert: false), scale)
        };
        stopwatch.Stop();
        return new PreprocessingBatch(variants, stopwatch.Elapsed.TotalMilliseconds);
    }

    public PreprocessingBatch CreateFastAmountVariants(BitmapSource source)
    {
        var stopwatch = Stopwatch.StartNew();
        var data = Prepare(source);
        var variants = new[]
        {
            Encode("Amount local contrast", data.Width, data.Height, CreateLocalContrast(data)),
            Encode("Amount bright low-saturation mask", data.Width, data.Height, CreateLowSaturationMask(data, invert: false)),
            Encode("Amount green suppression inverted", data.Width, data.Height, CreateGreenSuppression(data, invert: true))
        };
        stopwatch.Stop();
        return new PreprocessingBatch(variants, stopwatch.Elapsed.TotalMilliseconds);
    }

    public PreprocessedImage CreateSignedSafetyVariant(BitmapSource source)
    {
        return new PreprocessedImage("Original amount signed safety", EncodePng(ConvertToBgra32(source)), 1);
    }

    public PreprocessedImage CreateCharacterVariant(BitmapSource source, Int32Rect bounds, string method)
    {
        var safeBounds = new Int32Rect(
            Math.Clamp(bounds.X, 0, source.PixelWidth - 1),
            Math.Clamp(bounds.Y, 0, source.PixelHeight - 1),
            Math.Clamp(bounds.Width, 1, source.PixelWidth - Math.Clamp(bounds.X, 0, source.PixelWidth - 1)),
            Math.Clamp(bounds.Height, 1, source.PixelHeight - Math.Clamp(bounds.Y, 0, source.PixelHeight - 1)));
        var crop = new CroppedBitmap(source, safeBounds);
        crop.Freeze();
        return new PreprocessedImage(method, EncodePng(crop), 1);
    }

    public PreprocessingBatch CreateFallbackVariants(
        BitmapSource source,
        ScannerDetectionMode mode)
    {
        var stopwatch = Stopwatch.StartNew();
        var data = Prepare(source);
        var legacyTextMask = CreateLegacyTextMask(data, invert: false);
        var closedTextMask = MorphologicalClose(legacyTextMask, data.Width, data.Height);
        var neutral80 = CreateNeutralBrightnessMask(data, 80);
        var variants = new List<PreprocessedImage>
        {
            Encode("Fallback low-saturation text 0.55x", data.Width, data.Height, legacyTextMask, 0.55),
            Encode("Fallback closed text 0.55x", data.Width, data.Height, closedTextMask, 0.55),
            Encode("Fallback neutral brightness 80 0.55x", data.Width, data.Height, neutral80, 0.55)
        };

        if (mode is ScannerDetectionMode.Balanced or ScannerDetectionMode.Thorough)
        {
            variants.Add(Encode("Fallback original 0.55x", data.Width, data.Height, data.Pixels, 0.55));
            variants.Add(Encode("Fallback grayscale 0.55x", data.Width, data.Height, CreateGrayscale(data), 0.55));
            variants.Add(Encode("Fallback adaptive contrast", data.Width, data.Height, CreateAdaptiveMask(data)));
        }

        if (mode == ScannerDetectionMode.Thorough)
        {
            variants.Add(Encode("Fallback global contrast 0.55x", data.Width, data.Height, CreateGlobalContrast(data), 0.55));
            variants.Add(Encode("Fallback inverted low-saturation", data.Width, data.Height, CreateLowSaturationMask(data, invert: true)));
        }

        stopwatch.Stop();
        return new PreprocessingBatch(variants, stopwatch.Elapsed.TotalMilliseconds);
    }

    private static ImageData Prepare(BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var bitmap = ConvertToBgra32(source);
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        var pixels = new byte[checked(width * height * 4)];
        bitmap.CopyPixels(pixels, width * 4, 0);
        var luminance = new byte[width * height];
        var saturation = new byte[width * height];
        for (var pixel = 0; pixel < luminance.Length; pixel++)
        {
            var offset = pixel * 4;
            var blue = pixels[offset];
            var green = pixels[offset + 1];
            var red = pixels[offset + 2];
            luminance[pixel] = (byte)Math.Clamp(
                (int)Math.Round((red * 0.299) + (green * 0.587) + (blue * 0.114)),
                0,
                255);
            saturation[pixel] = (byte)(Math.Max(red, Math.Max(green, blue)) -
                Math.Min(red, Math.Min(green, blue)));
        }

        return new ImageData(width, height, pixels, luminance, saturation);
    }

    private static byte[] CreateLocalContrast(ImageData data)
    {
        var integral = CreateIntegral(data.Luminance, data.Width, data.Height);
        var output = new byte[data.Pixels.Length];
        var radius = Math.Clamp(Math.Min(data.Width, data.Height) / 18, 5, 28);
        for (var y = 0; y < data.Height; y++)
        {
            var top = Math.Max(0, y - radius);
            var bottom = Math.Min(data.Height - 1, y + radius);
            for (var x = 0; x < data.Width; x++)
            {
                var left = Math.Max(0, x - radius);
                var right = Math.Min(data.Width - 1, x + radius);
                var localMean = GetMean(integral, data.Width + 1, left, top, right, bottom);
                var value = data.Luminance[(y * data.Width) + x];
                var enhanced = (byte)Math.Clamp((int)Math.Round(128 + ((value - localMean) * 2.1)), 0, 255);
                SetGray(output, ((y * data.Width) + x) * 4, enhanced);
            }
        }

        return output;
    }

    private static byte[] CreateGrayscale(ImageData data)
    {
        var output = new byte[data.Pixels.Length];
        for (var index = 0; index < data.Luminance.Length; index++)
        {
            SetGray(output, index * 4, data.Luminance[index]);
        }

        return output;
    }

    private static byte[] CreateGlobalContrast(ImageData data)
    {
        var output = new byte[data.Pixels.Length];
        for (var index = 0; index < data.Luminance.Length; index++)
        {
            var value = (byte)Math.Clamp(
                (int)Math.Round(((data.Luminance[index] - 105) * 1.85) + 128),
                0,
                255);
            SetGray(output, index * 4, value);
        }

        return output;
    }

    private static byte[] CreateLowSaturationMask(ImageData data, bool invert)
    {
        var integral = CreateIntegral(data.Luminance, data.Width, data.Height);
        var output = new byte[data.Pixels.Length];
        var radius = Math.Clamp(Math.Min(data.Width, data.Height) / 24, 4, 20);
        for (var y = 0; y < data.Height; y++)
        {
            var top = Math.Max(0, y - radius);
            var bottom = Math.Min(data.Height - 1, y + radius);
            for (var x = 0; x < data.Width; x++)
            {
                var index = (y * data.Width) + x;
                var left = Math.Max(0, x - radius);
                var right = Math.Min(data.Width - 1, x + radius);
                var mean = GetMean(integral, data.Width + 1, left, top, right, bottom);
                var isText = data.Luminance[index] >= 105 &&
                    data.Luminance[index] >= mean + 8 &&
                    data.Saturation[index] <= 105;
                var value = isText ^ invert ? (byte)255 : (byte)0;
                SetGray(output, index * 4, value);
            }
        }

        return output;
    }

    private static byte[] CreateLegacyTextMask(ImageData data, bool invert)
    {
        var output = new byte[data.Pixels.Length];
        for (var index = 0; index < data.Luminance.Length; index++)
        {
            var isText = data.Luminance[index] >= 100 && data.Saturation[index] <= 118;
            var value = isText ^ invert ? (byte)255 : (byte)0;
            SetGray(output, index * 4, value);
        }

        return output;
    }

    private static byte[] CreateNeutralBrightnessMask(ImageData data, byte threshold)
    {
        var output = new byte[data.Pixels.Length];
        for (var index = 0; index < data.Luminance.Length; index++)
        {
            var offset = index * 4;
            var minimumChannel = Math.Min(
                data.Pixels[offset],
                Math.Min(data.Pixels[offset + 1], data.Pixels[offset + 2]));
            SetGray(output, offset, minimumChannel >= threshold ? (byte)255 : (byte)0);
        }

        return output;
    }

    private static byte[] MorphologicalClose(byte[] source, int width, int height)
    {
        var dilated = Morph(source, width, height, dilate: true);
        return Morph(dilated, width, height, dilate: false);
    }

    private static byte[] MorphologicalOpen(byte[] source, int width, int height)
    {
        var eroded = Morph(source, width, height, dilate: false);
        return Morph(eroded, width, height, dilate: true);
    }

    private static byte[] Morph(byte[] source, int width, int height, bool dilate)
    {
        var output = new byte[source.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var selected = dilate ? (byte)0 : (byte)255;
                for (var offsetY = -2; offsetY <= 2; offsetY++)
                {
                    var sampleY = Math.Clamp(y + offsetY, 0, height - 1);
                    for (var offsetX = -2; offsetX <= 2; offsetX++)
                    {
                        var sampleX = Math.Clamp(x + offsetX, 0, width - 1);
                        var value = source[((sampleY * width) + sampleX) * 4];
                        if (dilate ? value > selected : value < selected)
                        {
                            selected = value;
                        }
                    }
                }

                SetGray(output, ((y * width) + x) * 4, selected);
            }
        }

        return output;
    }

    private static byte[] InvertMask(byte[] source)
    {
        var output = new byte[source.Length];
        for (var index = 0; index < source.Length; index += 4)
        {
            SetGray(output, index, (byte)(255 - source[index]));
        }

        return output;
    }

    private static byte[] CreateGreenSuppression(ImageData data, bool invert)
    {
        var output = new byte[data.Pixels.Length];
        for (var index = 0; index < data.Luminance.Length; index++)
        {
            var offset = index * 4;
            var blue = data.Pixels[offset];
            var green = data.Pixels[offset + 1];
            var red = data.Pixels[offset + 2];
            var greenDominance = green - ((red + blue) / 2);
            var isText = data.Luminance[index] >= 105 &&
                data.Saturation[index] <= 112 &&
                greenDominance < 42;
            var value = isText ^ invert ? (byte)255 : (byte)0;
            SetGray(output, offset, value);
        }

        return output;
    }

    private static byte[] CreateAdaptiveMask(ImageData data)
    {
        var integral = CreateIntegral(data.Luminance, data.Width, data.Height);
        var output = new byte[data.Pixels.Length];
        var radius = Math.Clamp(Math.Min(data.Width, data.Height) / 22, 5, 24);
        for (var y = 0; y < data.Height; y++)
        {
            var top = Math.Max(0, y - radius);
            var bottom = Math.Min(data.Height - 1, y + radius);
            for (var x = 0; x < data.Width; x++)
            {
                var left = Math.Max(0, x - radius);
                var right = Math.Min(data.Width - 1, x + radius);
                var mean = GetMean(integral, data.Width + 1, left, top, right, bottom);
                var index = (y * data.Width) + x;
                var value = data.Luminance[index] >= Math.Max(82, mean + 12) ? (byte)255 : (byte)0;
                SetGray(output, index * 4, value);
            }
        }

        return output;
    }

    private static long[] CreateIntegral(byte[] values, int width, int height)
    {
        var stride = width + 1;
        var integral = new long[stride * (height + 1)];
        for (var y = 0; y < height; y++)
        {
            long row = 0;
            for (var x = 0; x < width; x++)
            {
                row += values[(y * width) + x];
                integral[((y + 1) * stride) + x + 1] = integral[(y * stride) + x + 1] + row;
            }
        }

        return integral;
    }

    private static double GetMean(long[] integral, int stride, int left, int top, int right, int bottom)
    {
        var sum = integral[((bottom + 1) * stride) + right + 1] -
            integral[(top * stride) + right + 1] -
            integral[((bottom + 1) * stride) + left] +
            integral[(top * stride) + left];
        return sum / (double)((right - left + 1) * (bottom - top + 1));
    }

    private static PreprocessedImage Encode(string name, int width, int height, byte[] pixels, double scale = 1)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        BitmapSource scaled = bitmap;
        if (scale != 1)
        {
            scaled = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
            scaled.Freeze();
        }

        return new PreprocessedImage(name, EncodePng(scaled), scale);
    }

    private static PreprocessedImage EncodeWithCoordinateScale(
        string name,
        int width,
        int height,
        byte[] pixels,
        double coordinateScale)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return new PreprocessedImage(name, EncodePng(bitmap), coordinateScale);
    }

    private static void SetGray(byte[] pixels, int offset, byte value)
    {
        pixels[offset] = value;
        pixels[offset + 1] = value;
        pixels[offset + 2] = value;
        pixels[offset + 3] = 255;
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

    private static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private sealed record ImageData(
        int Width,
        int Height,
        byte[] Pixels,
        byte[] Luminance,
        byte[] Saturation);
}
