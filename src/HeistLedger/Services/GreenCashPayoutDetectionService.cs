using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GTAEarningsTracker.Models;

namespace GTAEarningsTracker.Services;

/// <summary>
/// Detects GTA's green circular cash icon and reads the positive payout drawn to
/// its right. It analyzes visible pixels only and always requires confirmation.
/// </summary>
public sealed class GreenCashPayoutDetectionService
{
    public static Int32Rect? LocateFullScreenIcon(BitmapSource source) => FindGreenCashIcon(source, true)?.Bounds;
    private static readonly Regex LeadingPlusRegex = new(
        @"^\s*\+\s*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly OcrService _amountOcrService;
    private readonly FastGtaPreprocessingService _preprocessing = new();

    public GreenCashPayoutDetectionService(OcrService amountOcrService)
    {
        _amountOcrService = amountOcrService;
    }

    public PayoutAnalysisResult? Analyze(
        IReadOnlyList<BitmapSource> frames,
        CancellationToken cancellationToken, bool fullScreen = false)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var icon = FindGreenCashIcon(frames[0], fullScreen);
        if (icon is null)
        {
            return null;
        }

        var cropBounds = CreateAmountCrop(icon.Bounds, frames[0]);
        if (fullScreen) cropBounds = TrimTrailingDesktop(frames[0], cropBounds, icon.Bounds.Height);
        if (cropBounds.Width < 40 || cropBounds.Height < 24)
        {
            return null;
        }

        var frameIndexes = SelectFrameIndexes(frames.Count);
        var readers = new List<AmountReader>();
        var diagnostics = new List<GtaAmountOcrDiagnostic>();
        BitmapSource? firstCrop = null;

        foreach (var frameIndex in frameIndexes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = frames[frameIndex];
            if (cropBounds.X + cropBounds.Width > frame.PixelWidth ||
                cropBounds.Y + cropBounds.Height > frame.PixelHeight)
            {
                continue;
            }

            var crop = new CroppedBitmap(frame, cropBounds);
            crop.Freeze();
            firstCrop ??= crop;
            var variants = new List<(string Family, PreprocessedImage Image)>
            {
                ("Green/white glyph mask", CreateGreenAndWhiteGlyphVariant(crop)),
                ("Neutral glyph mask", _preprocessing.CreateLowSaturationVariant(crop))
            };
            if (frameIndex == frameIndexes[0])
            {
                variants.Add(("Local contrast", _preprocessing.CreateLocalContrastVariant(crop)));
                variants.Add(("Original pixels", _preprocessing.CreateSignedSafetyVariant(crop)));
            }

            foreach (var (family, variant) in variants)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var recognition = _amountOcrService.RecognizeAmount(
                    variant,
                    OcrAmountPageSegmentation.RawLine,
                    cancellationToken);
                var valid = TryParsePositiveAmount(recognition.Text, out var normalized, out var amount);
                diagnostics.Add(new GtaAmountOcrDiagnostic(
                    $"Green icon frame {frameIndex + 1}: {family}",
                    "RawLine",
                    recognition.Text,
                    valid ? normalized : string.Empty,
                    QuickCapturePolicy.NormalizeConfidence(recognition.Confidence),
                    valid ? amount : null,
                    SummarizeSymbols(recognition.Symbols)));
                if (valid)
                {
                    readers.Add(new AmountReader(
                        amount,
                        recognition.Text,
                        QuickCapturePolicy.NormalizeConfidence(recognition.Confidence),
                        family,
                        frameIndex,
                        variant));
                    var guideDigits = amount.ToString(CultureInfo.InvariantCulture);
                    if (family == "Green/white glyph mask" &&
                        HasMatchingDigitGeometry(crop, guideDigits, out var glyphCount))
                    {
                        readers.Add(new AmountReader(
                            amount,
                            recognition.Text,
                            Math.Max(0.55f, QuickCapturePolicy.NormalizeConfidence(recognition.Confidence)),
                            "Separated glyph geometry",
                            frameIndex,
                            variant));
                        diagnostics.Add(new GtaAmountOcrDiagnostic(
                            $"Green icon frame {frameIndex + 1}: Separated glyph geometry",
                            "Connected glyph validation",
                            recognition.Text,
                            normalized,
                            0.55f,
                            amount,
                            $"{glyphCount} separated digit glyphs match {guideDigits.Length} OCR digits"));
                    }
                }
            }
        }

        var groups = readers
            .GroupBy(reader => reader.Amount)
            .Select(group => new
            {
                Amount = group.Key,
                Readers = group.ToList(),
                FrameAgreement = group.Select(reader => reader.FrameIndex).Distinct().Count(),
                FamilyAgreement = group.Select(reader => reader.Family).Distinct(StringComparer.Ordinal).Count(),
                AverageConfidence = group.Average(reader => reader.Confidence)
            })
            .OrderByDescending(group => group.FrameAgreement)
            .ThenByDescending(group => group.FamilyAgreement)
            .ThenByDescending(group => group.Readers.Count)
            .ThenByDescending(group => group.AverageConfidence)
            .ToList();
        if (groups.Count == 0)
        {
            return null;
        }

        var winner = groups[0];
        var runnerUp = groups.Skip(1).FirstOrDefault();
        var requiredFrameAgreement = frameIndexes.Count > 1 ? 2 : 1;
        var competingTie = runnerUp is not null &&
            runnerUp.FrameAgreement >= winner.FrameAgreement &&
            runnerUp.FamilyAgreement >= winner.FamilyAgreement;
        if (competingTie || winner.FrameAgreement < requiredFrameAgreement || winner.FamilyAgreement < 2)
        {
            return null;
        }

        var representative = winner.Readers.OrderByDescending(reader => reader.Confidence).First();
        var bestProcessedPreview = GtaAmountPreprocessingService.DecodePreview(representative.Variant);
        var confidence = (float)Math.Clamp(
            (icon.Score * 0.35) +
            (Math.Min(1d, winner.FrameAgreement / 3d) * 0.30) +
            (Math.Min(1d, winner.FamilyAgreement / 3d) * 0.20) +
            (winner.AverageConfidence * 0.15),
            0,
            1);
        var competingCandidates = groups.Count == 1
            ? "None"
            : string.Join(", ", groups.Skip(1).Take(3).Select(group =>
                $"{group.Amount.ToString("$#,##0", CultureInfo.GetCultureInfo("en-US"))} ({group.Readers.Count} readers)"));
        var ambiguous = groups.Skip(1).Any(group =>
            group.FrameAgreement >= winner.FrameAgreement - 1 &&
            group.FamilyAgreement >= winner.FamilyAgreement - 1);
        var fingerprint = ComputeFingerprint(frames[0]);
        var rawText = representative.RawText.Trim();
        var candidate = new DetectedPayout(
            winner.Amount,
            rawText,
            $"GREEN CASH ICON | {winner.Amount.ToString(CultureInfo.InvariantCulture)}",
            PayoutDetectionMethod.GreenCashIconPayout,
            $"Green cash icon consensus: {representative.Family}",
            confidence,
            fingerprint,
            ambiguous,
            false);
        var scannerDiagnostics = new GtaResultScreenDiagnostics(
            true,
            "Green circular cash icon",
            (float)icon.Score,
            icon.Bounds,
            cropBounds,
            winner.Amount,
            confidence,
            representative.Family,
            "RawLine",
            winner.Readers.Count,
            competingCandidates,
            false,
            "Manual confirmation is always required for green-icon payouts.",
            diagnostics,
            AmountCropPreview: firstCrop,
            BestProcessedAmountPreview: bestProcessedPreview,
            StableFrameCount: winner.FrameAgreement,
            IndependentReaderAgreement: Math.Min(3, winner.FamilyAgreement),
            UncertainDigitPositions: ambiguous ? "Reader disagreement" : "None",
            TesseractAmount: winner.Amount,
            PaddleOcrStatus: "Not used for green-icon path");

        return new PayoutAnalysisResult(
            candidate,
            rawText,
            confidence,
            candidate.PreprocessingMethod,
            fingerprint,
            scannerDiagnostics);
    }

    public static bool TryParsePositiveAmount(
        string rawText,
        out string normalizedText,
        out long amount)
    {
        normalizedText = string.Empty;
        amount = 0;
        if (string.IsNullOrWhiteSpace(rawText) || rawText.Contains('-', StringComparison.Ordinal))
        {
            return false;
        }

        var withoutPlus = LeadingPlusRegex.Replace(rawText, string.Empty);
        return GtaResultScreenDetectionService.TryNormalizeAmountText(
            withoutPlus,
            out normalizedText,
            out amount,
            out _,
            out _);
    }

    private static bool HasMatchingDigitGeometry(
        BitmapSource crop,
        string guideDigits,
        out int glyphCount)
    {
        glyphCount = 0;
        var mask = GtaAmountPreprocessingService.DecodePreview(CreateGreenAndWhiteGlyphVariant(crop));
        var bitmap = ConvertToBgra32(mask);
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);
        var foreground = new bool[width * height];
        for (var index = 0; index < foreground.Length; index++)
        {
            foreground[index] = pixels[index * 4] >= 200;
        }

        var visited = new bool[foreground.Length];
        var queue = new int[foreground.Length];
        var components = new List<Int32Rect>();
        var minimumHeight = Math.Max(18, (int)Math.Round(height * 0.42));
        var maximumHeight = Math.Max(minimumHeight, (int)Math.Round(height * 0.76));
        var minimumWidth = Math.Max(8, (int)Math.Round(height * 0.055));
        var maximumWidth = Math.Max(minimumWidth, (int)Math.Round(height * 0.55));
        var minimumLeft = (int)Math.Round(width * 0.18);
        for (var index = 0; index < foreground.Length; index++)
        {
            if (!foreground[index] || visited[index])
            {
                continue;
            }

            var head = 0;
            var tail = 0;
            queue[tail++] = index;
            visited[index] = true;
            var minX = width;
            var minY = height;
            var maxX = 0;
            var maxY = 0;
            var pixelCount = 0;
            while (head < tail)
            {
                var current = queue[head++];
                var x = current % width;
                var y = current / width;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
                pixelCount++;
                Visit(x - 1, y);
                Visit(x + 1, y);
                Visit(x, y - 1);
                Visit(x, y + 1);
            }

            var componentWidth = maxX - minX + 1;
            var componentHeight = maxY - minY + 1;
            if (minX >= minimumLeft &&
                componentHeight >= minimumHeight && componentHeight <= maximumHeight &&
                componentWidth >= minimumWidth && componentWidth <= maximumWidth &&
                pixelCount >= componentWidth * componentHeight * 0.10)
            {
                components.Add(new Int32Rect(minX, minY, componentWidth, componentHeight));
            }

            void Visit(int x, int y)
            {
                if (x < 0 || x >= width || y < 0 || y >= height)
                {
                    return;
                }

                var neighbor = (y * width) + x;
                if (!foreground[neighbor] || visited[neighbor])
                {
                    return;
                }

                visited[neighbor] = true;
                queue[tail++] = neighbor;
            }
        }

        var ordered = components.OrderBy(component => component.X).ToList();
        if (ordered.Count is < 3 or > 10)
        {
            return false;
        }

        var maximumGlyphHeight = ordered.Max(component => component.Height);
        ordered = ordered
            .Where(component => component.Height >= maximumGlyphHeight * 0.78)
            .ToList();
        if (ordered.Count is < 3 or > 10)
        {
            return false;
        }

        glyphCount = ordered.Count;
        if (glyphCount != guideDigits.Length)
        {
            return false;
        }

        var averageWidth = ordered.Average(component => component.Width);
        var gaps = ordered.Zip(ordered.Skip(1), (left, right) => right.X - (left.X + left.Width)).ToArray();
        return gaps.All(gap => gap >= 0 && gap <= averageWidth * 0.75);
    }

    private static GreenIconObservation? FindGreenCashIcon(BitmapSource source, bool fullScreen = false)
    {
        var bitmap = ConvertToBgra32(source);
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        bitmap.CopyPixels(pixels, stride, 0);
        var searchWidth = Math.Max(1, (int)Math.Round(width * (fullScreen ? 1 : 0.52)));
        var mask = new bool[checked(searchWidth * height)];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < searchWidth; x++)
            {
                var offset = (y * stride) + (x * 4);
                var blue = pixels[offset];
                var green = pixels[offset + 1];
                var red = pixels[offset + 2];
                mask[(y * searchWidth) + x] =
                    green >= 82 && red >= 34 && blue >= 30 &&
                    green - red >= 24 && green - blue >= 18;
            }
        }

        var visited = new bool[mask.Length];
        var queue = new int[mask.Length];
        var candidates = new List<GreenIconObservation>();
        var minimumDiameter = Math.Max(36, (int)Math.Round(height * (fullScreen ? 0.025 : 0.24)));
        var maximumDiameter = Math.Max(minimumDiameter, Math.Min(height, (int)Math.Round(width * 0.45)));
        for (var index = 0; index < mask.Length; index++)
        {
            if (!mask[index] || visited[index])
            {
                continue;
            }

            var head = 0;
            var tail = 0;
            queue[tail++] = index;
            visited[index] = true;
            var minX = searchWidth;
            var minY = height;
            var maxX = 0;
            var maxY = 0;
            var count = 0;
            while (head < tail)
            {
                var current = queue[head++];
                var x = current % searchWidth;
                var y = current / searchWidth;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
                count++;
                Visit(x - 1, y);
                Visit(x + 1, y);
                Visit(x, y - 1);
                Visit(x, y + 1);
            }

            var componentWidth = maxX - minX + 1;
            var componentHeight = maxY - minY + 1;
            if (componentWidth < minimumDiameter || componentHeight < minimumDiameter ||
                componentWidth > maximumDiameter || componentHeight > maximumDiameter)
            {
                continue;
            }

            var aspect = componentWidth / (double)componentHeight;
            var area = componentWidth * componentHeight;
            var greenCoverage = count / (double)area;
            if (aspect is < 0.76 or > 1.24 || greenCoverage is < 0.40 or > 0.84)
            {
                continue;
            }

            var whitePixels = 0;
            for (var y = minY; y <= maxY; y++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    var offset = (y * stride) + (x * 4);
                    var blue = pixels[offset];
                    var green = pixels[offset + 1];
                    var red = pixels[offset + 2];
                    var maximum = Math.Max(red, Math.Max(green, blue));
                    var minimum = Math.Min(red, Math.Min(green, blue));
                    if (minimum >= 170 && maximum - minimum <= 52)
                    {
                        whitePixels++;
                    }
                }
            }

            var whiteCoverage = whitePixels / (double)area;
            if (whiteCoverage is < 0.025 or > 0.34)
            {
                continue;
            }

            var shapeScore = 1 - Math.Min(1, Math.Abs(1 - aspect));
            var densityScore = 1 - Math.Min(1, Math.Abs(0.64 - greenCoverage) / 0.30);
            var whiteScore = 1 - Math.Min(1, Math.Abs(0.12 - whiteCoverage) / 0.20);
            var score = Math.Clamp((shapeScore * 0.42) + (densityScore * 0.38) + (whiteScore * 0.20), 0, 1);
            candidates.Add(new GreenIconObservation(
                new Int32Rect(minX, minY, componentWidth, componentHeight),
                score));

            void Visit(int x, int y)
            {
                if (x < 0 || x >= searchWidth || y < 0 || y >= height)
                {
                    return;
                }

                var neighbor = (y * searchWidth) + x;
                if (!mask[neighbor] || visited[neighbor])
                {
                    return;
                }

                visited[neighbor] = true;
                queue[tail++] = neighbor;
            }
        }

        var broadColorCandidate = candidates
            .Where(candidate => candidate.Score >= 0.58)
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Bounds.Width * candidate.Bounds.Height)
            .FirstOrDefault();
        return broadColorCandidate ?? FindUniformGreenIcon(
            pixels,
            width,
            height,
            stride,
            searchWidth,
            minimumDiameter,
            maximumDiameter);
    }

    private static GreenIconObservation? FindUniformGreenIcon(
        byte[] pixels,
        int width,
        int height,
        int stride,
        int searchWidth,
        int minimumDiameter,
        int maximumDiameter)
    {
        // A translucent logo can touch similarly green scenery when using a broad
        // hue mask. GTA renders the logo fill with a highly consistent color, so
        // a second color-cluster pass separates that circle from the background.
        var histogram = new Dictionary<int, int>();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < searchWidth; x++)
            {
                var offset = (y * stride) + (x * 4);
                var blue = pixels[offset];
                var green = pixels[offset + 1];
                var red = pixels[offset + 2];
                if (green < 78 || red < 30 || blue < 28 ||
                    green - red < 20 || green - blue < 15)
                {
                    continue;
                }

                var key = ((red >> 3) << 10) | ((green >> 3) << 5) | (blue >> 3);
                histogram[key] = histogram.GetValueOrDefault(key) + 1;
            }
        }

        var minimumClusterPixels = Math.Max(300, minimumDiameter * minimumDiameter / 18);
        var colorClusters = histogram
            .Where(pair => pair.Value >= minimumClusterPixels)
            .OrderByDescending(pair => pair.Value)
            .Take(10)
            .Select(pair => pair.Key)
            .ToArray();
        var observations = new List<GreenIconObservation>();
        foreach (var colorKey in colorClusters)
        {
            var centerRed = (((colorKey >> 10) & 31) * 8) + 4;
            var centerGreen = (((colorKey >> 5) & 31) * 8) + 4;
            var centerBlue = ((colorKey & 31) * 8) + 4;
            var mask = new bool[checked(searchWidth * height)];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < searchWidth; x++)
                {
                    var offset = (y * stride) + (x * 4);
                    var blue = pixels[offset];
                    var green = pixels[offset + 1];
                    var red = pixels[offset + 2];
                    mask[(y * searchWidth) + x] =
                        Math.Abs(red - centerRed) <= 11 &&
                        Math.Abs(green - centerGreen) <= 11 &&
                        Math.Abs(blue - centerBlue) <= 11;
                }
            }

            var visited = new bool[mask.Length];
            var queue = new int[mask.Length];
            for (var index = 0; index < mask.Length; index++)
            {
                if (!mask[index] || visited[index])
                {
                    continue;
                }

                var head = 0;
                var tail = 0;
                queue[tail++] = index;
                visited[index] = true;
                var minX = searchWidth;
                var minY = height;
                var maxX = 0;
                var maxY = 0;
                var count = 0;
                while (head < tail)
                {
                    var current = queue[head++];
                    var x = current % searchWidth;
                    var y = current / searchWidth;
                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                    count++;
                    Visit(x - 1, y);
                    Visit(x + 1, y);
                    Visit(x, y - 1);
                    Visit(x, y + 1);
                }

                var componentWidth = maxX - minX + 1;
                var componentHeight = maxY - minY + 1;
                if (componentWidth < minimumDiameter || componentHeight < minimumDiameter ||
                    componentWidth > maximumDiameter || componentHeight > maximumDiameter)
                {
                    continue;
                }

                var aspect = componentWidth / (double)componentHeight;
                var area = componentWidth * componentHeight;
                var colorCoverage = count / (double)area;
                if (aspect is < 0.76 or > 1.24 || colorCoverage is < 0.38 or > 0.84)
                {
                    continue;
                }

                var whitePixels = 0;
                for (var sampleY = minY; sampleY <= maxY; sampleY++)
                {
                    for (var sampleX = minX; sampleX <= maxX; sampleX++)
                    {
                        var offset = (sampleY * stride) + (sampleX * 4);
                        var blue = pixels[offset];
                        var green = pixels[offset + 1];
                        var red = pixels[offset + 2];
                        var maximum = Math.Max(red, Math.Max(green, blue));
                        var minimum = Math.Min(red, Math.Min(green, blue));
                        if (minimum >= 168 && maximum - minimum <= 55)
                        {
                            whitePixels++;
                        }
                    }
                }

                var whiteCoverage = whitePixels / (double)area;
                if (whiteCoverage is < 0.025 or > 0.34)
                {
                    continue;
                }

                var shapeScore = 1 - Math.Min(1, Math.Abs(1 - aspect));
                var densityScore = 1 - Math.Min(1, Math.Abs(0.62 - colorCoverage) / 0.30);
                var whiteScore = 1 - Math.Min(1, Math.Abs(0.12 - whiteCoverage) / 0.20);
                var score = Math.Clamp(
                    (shapeScore * 0.42) + (densityScore * 0.38) + (whiteScore * 0.20),
                    0,
                    1);
                observations.Add(new GreenIconObservation(
                    new Int32Rect(minX, minY, componentWidth, componentHeight),
                    score));

                void Visit(int x, int y)
                {
                    if (x < 0 || x >= searchWidth || y < 0 || y >= height)
                    {
                        return;
                    }

                    var neighbor = (y * searchWidth) + x;
                    if (!mask[neighbor] || visited[neighbor])
                    {
                        return;
                    }

                    visited[neighbor] = true;
                    queue[tail++] = neighbor;
                }
            }
        }

        return observations
            .Where(candidate => candidate.Score >= 0.58)
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Bounds.Width * candidate.Bounds.Height)
            .FirstOrDefault();
    }

    private static Int32Rect CreateAmountCrop(Int32Rect iconBounds, BitmapSource source)
    {
        var diameter = Math.Max(iconBounds.Width, iconBounds.Height);
        var left = Math.Clamp(
            iconBounds.X + iconBounds.Width + (int)Math.Round(diameter * 0.025),
            0,
            source.PixelWidth - 1);
        var top = Math.Clamp(iconBounds.Y - (int)Math.Round(diameter * 0.04), 0, source.PixelHeight - 1);
        var bottom = Math.Clamp(
            iconBounds.Y + iconBounds.Height + (int)Math.Round(diameter * 0.04),
            top + 1,
            source.PixelHeight);
        return new Int32Rect(left, top, source.PixelWidth - left, bottom - top);
    }

    private static Int32Rect TrimTrailingDesktop(BitmapSource source, Int32Rect bounds, int diameter)
    {
        var crop = ConvertToBgra32(new CroppedBitmap(source, bounds));
        var stride = crop.PixelWidth * 4;
        var pixels = new byte[stride * crop.PixelHeight];
        crop.CopyPixels(pixels, stride, 0);
        var last = -1;
        for (var x = 0; x < crop.PixelWidth; x++)
        {
            var count = 0;
            for (var y = 0; y < crop.PixelHeight; y++)
            {
                var i = y * stride + x * 4;
                if (Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2])) >= 145) count++;
            }
            if (count >= Math.Max(6, diameter * 0.10)) last = x;
            else if (last >= 0 && x - last > diameter * 0.45) break;
        }
        return last < diameter / 2 ? bounds : new Int32Rect(bounds.X, bounds.Y,
            Math.Min(bounds.Width, last + Math.Max(12, diameter / 12)), bounds.Height);
    }

    private static IReadOnlyList<int> SelectFrameIndexes(int frameCount)
    {
        return frameCount <= 1
            ? [0]
            : new[] { 0, frameCount / 2, frameCount - 1 }.Distinct().ToArray();
    }

    private static PreprocessedImage CreateGreenAndWhiteGlyphVariant(BitmapSource source)
    {
        var bitmap = ConvertToBgra32(source);
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);
        var output = new byte[pixels.Length];
        for (var index = 0; index < width * height; index++)
        {
            var offset = index * 4;
            var blue = pixels[offset];
            var green = pixels[offset + 1];
            var red = pixels[offset + 2];
            var maximum = Math.Max(red, Math.Max(green, blue));
            var minimum = Math.Min(red, Math.Min(green, blue));
            var lowSaturationBright = maximum >= 145 && maximum - minimum <= 125;
            var brightGreen = green >= 155 && green - red >= 22 && green - blue >= 12;
            var value = lowSaturationBright || brightGreen ? (byte)255 : (byte)0;
            output[offset] = value;
            output[offset + 1] = value;
            output[offset + 2] = value;
            output[offset + 3] = 255;
        }

        var result = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            output,
            stride);
        result.Freeze();
        return new PreprocessedImage("Green/white payout glyph mask", EncodePng(result), 1);
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

    private static string ComputeFingerprint(BitmapSource source)
    {
        var bitmap = ConvertToBgra32(source);
        var stride = checked(bitmap.PixelWidth * 4);
        var pixels = new byte[checked(stride * bitmap.PixelHeight)];
        bitmap.CopyPixels(pixels, stride, 0);
        return Convert.ToHexString(SHA256.HashData(pixels));
    }

    private static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static string SummarizeSymbols(IReadOnlyList<OcrSymbol>? symbols)
    {
        return symbols is not { Count: > 0 }
            ? "None"
            : string.Join(" ", symbols.OrderBy(symbol => symbol.Left).Select(symbol =>
                $"{symbol.Text}@{symbol.Left}-{symbol.Right}"));
    }

    private sealed record GreenIconObservation(Int32Rect Bounds, double Score);

    private sealed record AmountReader(
        long Amount,
        string RawText,
        float Confidence,
        string Family,
        int FrameIndex,
        PreprocessedImage Variant);
}
