using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GTAEarningsTracker.Models;
using GTAEarningsTracker.Services;

namespace PaddleOcrRegressionTests;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var failures = new List<string>();
        RunConsensusTests(failures);
        RunMissingModelTest(failures);

        using var generalOcr = new OcrService();
        using var headingOcr = new OcrService(OcrWorkerKind.Heading);
        using var amountOcr = new OcrService(OcrWorkerKind.Amount);
        using var detector = new PayoutDetectionService(
            new ImagePreprocessingService(), generalOcr, headingOcr, amountOcr);
        using var paddle = new PaddleOcrRecognitionService();

        var cases = new[]
        {
            ("GtaAmountMisread_1818663.png", 1_818_663L),
            ("GtaTotalCashEarnedReference.png", 12_418_670L),
            ("test 1.png", 968_000L),
            ("test 2.png", 1_413_716L),
            ("test 3.png", 968_000L),
            ("test 4.png", 2_538_000L),
            ("test 5.png", 3_615_920L),
            ("test 8.png", 1_188_830L),
            ("test 9.png", 864_000L),
            ("test 10.png", 13_875_000L),
            ("Screenshot 2026-07-15 202447.png", 3_485_830L),
            ("Screenshot 2026-07-15 202518.png", 2_491_190L),
            ("Screenshot 2026-07-15 202555.png", 2_540_540L)
        };

        Console.WriteLine($"Model: {PaddleOcrRecognitionService.ModelDisplayName}");
        Console.WriteLine($"Status: {paddle.ModelStatus}; load={paddle.ModelLoadDuration?.TotalMilliseconds:0.0} ms");
        foreach (var (fileName, expected) in cases)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "TestSamples", fileName);
            if (!File.Exists(path))
            {
                failures.Add($"missing {fileName}");
                continue;
            }

            var image = LoadImage(path);
            var analysis = detector.AnalyzeSelectedRegionProbe(
                image, ScannerDetectionMode.Fast, CancellationToken.None);
            var diagnostics = analysis.GtaResultDiagnostics;
            if (diagnostics?.AnchorFound != true || !IsValidCrop(diagnostics.AmountCropBounds, image))
            {
                Console.WriteLine($"{fileName}: heading absent; PaddleOCR not run");
                failures.Add($"anchor {fileName}");
                continue;
            }

            var crop = new CroppedBitmap(image, diagnostics.AmountCropBounds);
            crop.Freeze();
            var result = paddle.Recognize(crop, CancellationToken.None);
            var preprocessing = new GtaAmountPreprocessingService();
            var processedResults = preprocessing.CreateVariants(crop)
                .Select(variant => (variant.Method, Result: paddle.Recognize(
                    GtaAmountPreprocessingService.DecodePreview(variant), CancellationToken.None)))
                .Where(item => item.Result.IsValid)
                .OrderByDescending(item => item.Result.Confidence)
                .ToArray();
            var best = processedResults.FirstOrDefault();
            Console.WriteLine(
                $"{fileName}: expected={expected}; raw={OneLine(result.RawText)}; " +
                $"digits={result.NormalizedDigits}; confidence={result.Confidence:P1}; " +
                $"duration={result.ProcessingDuration.TotalMilliseconds:0.0} ms; error={result.Error ?? "none"}; " +
                $"bestProcessed={best.Result?.NormalizedDigits ?? "none"} " +
                $"({best.Method ?? "--"}, {best.Result?.Confidence:P1})");
        }

        RunIntegratedMisreadRegression(detector, amountOcr, paddle, failures);

        RunHeadingAbsentTest(detector, amountOcr, failures);
        if (failures.Count == 0)
        {
            Console.WriteLine("All PaddleOCR service and consensus tests passed.");
            return 0;
        }

        Console.Error.WriteLine($"Failed: {string.Join(", ", failures)}");
        return 1;
    }

    private static void RunConsensusTests(ICollection<string> failures)
    {
        Assert("all agree eligible", 1_818_663, 1_818_663, 1_818_663, 2, 2, false, false, true);
        Assert("Tesseract wrong", 1_816_663, 1_818_663, 1_818_663, 2, 2, false, false, false);
        Assert("Paddle wrong", 1_818_663, 1_816_663, 1_818_663, 2, 2, false, false, false);
        Assert("all disagree", 1_816_663, 1_818_663, 1_818_683, 2, 2, false, false, false);

        void Assert(
            string name, long? tesseract, long? paddle, long? digits,
            int tesseractSupport, int paddleSupport, bool uncertain, bool competing, bool eligible)
        {
            var decision = GtaLargeDigitReaderService.EvaluateEngineConsensus(
                tesseract, paddle, digits, tesseractSupport, paddleSupport, uncertain, competing);
            if (decision.EligibleForAutomaticAdd != eligible)
            {
                failures.Add(name);
            }
        }
    }

    private static void RunMissingModelTest(ICollection<string> failures)
    {
        using var missing = new PaddleOcrRecognitionService(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)));
        if (missing.IsAvailable || missing.ModelStatus != PaddleOcrRecognitionService.UnavailableMessage)
        {
            failures.Add("missing model fallback");
        }
    }

    private static void RunHeadingAbsentTest(
        PayoutDetectionService detector,
        OcrService amountOcr,
        ICollection<string> failures)
    {
        var pixels = new byte[640 * 240 * 4];
        var blank = BitmapSource.Create(640, 240, 96, 96, PixelFormats.Bgra32, null, pixels, 640 * 4);
        blank.Freeze();
        using var paddle = new PaddleOcrRecognitionService();
        var reader = new GtaLargeDigitReaderService(detector, amountOcr, paddle);
        var result = reader.Analyze([blank, blank, blank], ScannerDetectionMode.Fast, CancellationToken.None);
        if (result.Analysis.GtaResultDiagnostics?.AnchorFound == true ||
            paddle.LatestProcessingDuration is not null)
        {
            failures.Add("heading absent PaddleOCR gate");
        }
    }

    private static void RunIntegratedMisreadRegression(
        PayoutDetectionService detector,
        OcrService amountOcr,
        PaddleOcrRecognitionService paddle,
        ICollection<string> failures)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestSamples", "GtaAmountMisread_1818663.png");
        var fullImage = LoadImage(path);
        var image = new CroppedBitmap(
            fullImage,
            new Int32Rect(0, 0, Math.Min(1632, fullImage.PixelWidth), fullImage.PixelHeight));
        image.Freeze();
        var reader = new GtaLargeDigitReaderService(detector, amountOcr, paddle);
        var result = reader.Analyze(
            Enumerable.Repeat<BitmapSource>(image, GtaLargeDigitReaderService.MaximumBurstFrames).ToArray(),
            ScannerDetectionMode.Fast,
            CancellationToken.None,
            forceTargetedVerification: true);
        Console.WriteLine(
            $"Integrated $1,818,663: selected={result.Analysis.Candidate?.Amount}; " +
            $"Tesseract={result.Analysis.GtaResultDiagnostics?.TesseractAmount}; " +
            $"Paddle={result.Analysis.GtaResultDiagnostics?.PaddleOcrAmount}; " +
            $"digits={result.Analysis.GtaResultDiagnostics?.DigitReaderAmount}; " +
            $"auto={result.Analysis.Candidate?.AutomaticAddRecommended}");
        if (result.Analysis.Candidate?.Amount != 1_818_663)
        {
            failures.Add("integrated 1818663 changed value");
        }
    }

    private static bool IsValidCrop(Int32Rect bounds, BitmapSource source) =>
        bounds.X >= 0 && bounds.Y >= 0 && bounds.Width > 0 && bounds.Height > 0 &&
        bounds.X + bounds.Width <= source.PixelWidth && bounds.Y + bounds.Height <= source.PixelHeight;

    private static BitmapSource LoadImage(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    private static string OneLine(string text) => string.IsNullOrWhiteSpace(text)
        ? "(empty)"
        : string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
