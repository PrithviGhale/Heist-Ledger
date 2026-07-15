using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GTAEarningsTracker.Models;
using GTAEarningsTracker.Services;

namespace GeneratedQuickCaptureTests;

internal static class Program
{
    private sealed record GeneratedCase(
        string Name,
        string[] Lines,
        long? ExpectedAmount,
        bool? ExpectAutomaticAdd);

    [STAThread]
    private static int Main(string[] args)
    {
        var generatedOnly = args.Contains("--generated-only", StringComparer.OrdinalIgnoreCase);
        var referenceOnly = args.Contains("--reference-only", StringComparer.OrdinalIgnoreCase);
        var userImagesOnly = args.Contains("--user-images-only", StringComparer.OrdinalIgnoreCase);
        var requestedUserImage = args.FirstOrDefault(argument =>
            argument.StartsWith("--user-image=", StringComparison.OrdinalIgnoreCase))?[13..];
        var requestedCase = args.FirstOrDefault(argument => argument.StartsWith("--case=", StringComparison.OrdinalIgnoreCase))?[7..];
        var cases = new[]
        {
            new GeneratedCase("500k anchored", ["TOTAL CASH EARNED", "$500,000"], 500_000, null),
            new GeneratedCase("1.25m anchored", ["TOTAL CASH EARNED", "$1,250,000"], 1_250_000, null),
            new GeneratedCase("no visible commas", ["TOTAL CASH EARNED", "$12118670"], 12_118_670, null),
            new GeneratedCase("minor EARNFD variation", ["TOTAL CASH EARNFD", "$750,000"], 750_000, null),
            new GeneratedCase("1818663 repeated 8 and 6", ["TOTAL CASH EARNED", "$1,818,663"], 1_818_663, null),
            new GeneratedCase("1816663 preserves 6", ["TOTAL CASH EARNED", "$1,816,663"], 1_816_663, null),
            new GeneratedCase("8686868 alternating digits", ["TOTAL CASH EARNED", "$8,686,868"], 8_686_868, null),
            new GeneratedCase("6868686 alternating digits", ["TOTAL CASH EARNED", "$6,868,686"], 6_868_686, null),
            new GeneratedCase("spent rejection", ["TOTAL CASH SPENT", "$500,000"], null, false),
            new GeneratedCase("unanchored rejection", ["$12,118,670"], null, false),
            new GeneratedCase("negative rejection", ["TOTAL CASH EARNED", "-$20,000"], null, false),
            new GeneratedCase(
                "conflicting amounts",
                ["TOTAL CASH EARNED", "$500,000", "$600,000"],
                null,
                false)
        };

        using var ocrService = new OcrService();
        using var headingOcrService = new OcrService(OcrWorkerKind.Heading);
        using var amountOcrService = new OcrService(OcrWorkerKind.Amount);
        var preprocessing = new ImagePreprocessingService();
        using var detection = new PayoutDetectionService(
            preprocessing,
            ocrService,
            headingOcrService,
            amountOcrService);
        var failures = new List<string>();

        if (userImagesOnly)
        {
            RunUserPngRegressions(detection, amountOcrService, failures, requestedUserImage);
        }
        else if (!generatedOnly)
        {
            RunReferenceRegression(detection, failures);
            RunReferenceQuickScanRegression(detection, amountOcrService, failures);
            RunMisreadRegression(detection, amountOcrService, failures);
            RunUserPngRegressions(detection, amountOcrService, failures, requestedUserImage);
        }

        foreach (var testCase in cases.Where(testCase =>
                     !userImagesOnly &&
                     !referenceOnly &&
                     (string.IsNullOrWhiteSpace(requestedCase) ||
                      testCase.Name.Contains(requestedCase, StringComparison.OrdinalIgnoreCase))))
        {
            var image = RenderResultImage(testCase.Lines);
            var analysis = detection.Analyze(image, CancellationToken.None);
            var candidate = GetSpecializedCandidate(analysis);
            var decision = QuickCapturePolicy.Evaluate(candidate, requireManualConfirmation: false);
            long? actualAmount = candidate?.Amount;
            var automaticallyAdds = decision == QuickCaptureDecision.AutomaticallyAdd;

            Console.WriteLine(
                $"{testCase.Name}: amount={actualAmount?.ToString(CultureInfo.InvariantCulture) ?? "none"}, " +
                $"confidence={analysis.Confidence?.ToString("0.000", CultureInfo.InvariantCulture) ?? "--"}, " +
                $"decision={decision}, " +
                $"duration={analysis.GtaResultDiagnostics?.Performance?.TotalScanMilliseconds.ToString("0.0", CultureInfo.InvariantCulture) ?? "--"} ms, " +
                $"passes={analysis.GtaResultDiagnostics?.Performance?.PreprocessingVariantsAttempted.ToString(CultureInfo.InvariantCulture) ?? "--"}");
            Console.WriteLine(
                $"  anchor={analysis.GtaResultDiagnostics?.AnchorFound}; " +
                $"anchorConfidence={analysis.GtaResultDiagnostics?.AnchorConfidence:P0}; " +
                $"anchorText={OneLine(analysis.GtaResultDiagnostics?.AnchorOcrText ?? string.Empty)}; " +
                $"agreeing={analysis.GtaResultDiagnostics?.AgreeingVariantCount}; " +
                $"competing={analysis.GtaResultDiagnostics?.CompetingCandidates}; " +
                $"reason={analysis.GtaResultDiagnostics?.AutomaticAddReason}");
            if (!string.IsNullOrWhiteSpace(requestedCase) && analysis.GtaResultDiagnostics is { } requestedDiagnostics)
            {
                foreach (var result in requestedDiagnostics.AmountOcrResults)
                {
                    Console.WriteLine(
                        $"    {result.PreprocessingMethod} / {result.PageSegmentationMode}: " +
                        $"raw={OneLine(result.RawText)}, normalized={result.NormalizedText}, confidence={result.Confidence:P0}");
                }
            }

            if (testCase.Name == "conflicting amounts")
            {
                if (automaticallyAdds)
                {
                    failures.Add(testCase.Name);
                }
            }
            else if (actualAmount != testCase.ExpectedAmount ||
                     (testCase.ExpectAutomaticAdd is bool expectedAutomaticAdd &&
                      automaticallyAdds != expectedAutomaticAdd))
            {
                failures.Add(testCase.Name);
            }

            if (testCase.Name is "spent rejection" or "unanchored rejection" &&
                analysis.GtaResultDiagnostics?.AmountOcrResults.Count != 0)
            {
                failures.Add($"{testCase.Name} ran amount OCR");
            }
        }

        if (!userImagesOnly)
        {
            RunNormalizationTests(failures);
            RunPolicyAndDuplicateTests(failures);
            RunFrameChangeTests(failures);
            RunSettingsCompatibilityTest(failures);
            RunSelectedRegionSafetyTests(failures);
            RunConsensusTests(failures);
            RunAccuracyFirstBurstTest(detection, amountOcrService, failures);
        }

        if (failures.Count == 0)
        {
            Console.WriteLine(generatedOnly
                ? "All generated production-pipeline OCR tests passed."
                : "All OCR regression tests passed.");
            return 0;
        }

        Console.Error.WriteLine($"Failed: {string.Join(", ", failures)}");
        return 1;
    }

    private static void RunReferenceRegression(
        PayoutDetectionService detection,
        ICollection<string> failures)
    {
        var referencePath = Path.Combine(
            AppContext.BaseDirectory,
            "TestSamples",
            "GtaTotalCashEarnedReference.png");
        if (!File.Exists(referencePath))
        {
            Console.Error.WriteLine(
                $"REFERENCE TEST BLOCKED: missing development asset {referencePath}");
            failures.Add("reference screenshot asset missing");
            return;
        }

        var image = LoadImage(referencePath);
        var analysis = detection.Analyze(image, CancellationToken.None);
        var candidate = GetSpecializedCandidate(analysis);
        var diagnostics = analysis.GtaResultDiagnostics;

        Console.WriteLine("Reference image per-variant OCR output:");
        if (diagnostics is not null)
        {
            Console.WriteLine(
                $"  heading: raw={OneLine(diagnostics.AnchorOcrText)}, confidence={diagnostics.AnchorConfidence:P0}, " +
                $"duration={diagnostics.Performance?.HeadingOcrMilliseconds:0.0} ms");
            foreach (var result in diagnostics.AmountOcrResults)
            {
                Console.WriteLine(
                    $"  {result.PreprocessingMethod} / {result.PageSegmentationMode} | " +
                    $"confidence={result.Confidence:P0} | raw={OneLine(result.RawText)} | " +
                    $"normalized={(string.IsNullOrWhiteSpace(result.NormalizedText) ? "--" : result.NormalizedText)} | " +
                    $"symbols={result.SymbolSummary}");
            }

            Console.WriteLine(
                $"Reference winner: {diagnostics.SelectedPreprocessingMethod} / " +
                $"{diagnostics.SelectedPageSegmentationMode}; auto-add={diagnostics.AutomaticAddAccepted}; " +
                $"amount={diagnostics.SelectedAmount?.ToString(CultureInfo.InvariantCulture) ?? "none"}; " +
                $"total={diagnostics.Performance?.TotalScanMilliseconds:0.0} ms; " +
                $"fallback={diagnostics.Performance?.FallbackMilliseconds:0.0} ms; " +
                $"passes={diagnostics.Performance?.PreprocessingVariantsAttempted}; " +
                $"reason={diagnostics.AutomaticAddReason}");
        }

        var amountIsBelowAnchor = diagnostics?.AnchorBounds is Int32Rect anchorBounds &&
            diagnostics.AmountCropBounds.Y >= anchorBounds.Y + anchorBounds.Height;
        if (diagnostics is null || !diagnostics.AnchorFound ||
            candidate?.Amount != 12_418_670 ||
            candidate.DetectionMethod != PayoutDetectionMethod.GtaTotalCashEarnedResultScreen ||
            !amountIsBelowAnchor)
        {
            failures.Add("reference screenshot detection");
        }
    }

    private static void RunMisreadRegression(
        PayoutDetectionService detection,
        OcrService amountOcrService,
        ICollection<string> failures)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestSamples", "GtaAmountMisread_1818663.png");
        if (!File.Exists(path))
        {
            failures.Add("1818663 regression screenshot asset missing");
            return;
        }

        var fullImage = LoadImage(path);
        var image = new CroppedBitmap(
            fullImage,
            new Int32Rect(0, 0, Math.Min(1632, fullImage.PixelWidth), fullImage.PixelHeight));
        image.Freeze();
        var burstReader = new GtaLargeDigitReaderService(detection, amountOcrService);
        var frames = Enumerable.Repeat<BitmapSource>(image, GtaLargeDigitReaderService.MinimumBurstFrames).ToList();
        var burst = burstReader.Analyze(
            frames,
            ScannerDetectionMode.Fast,
            CancellationToken.None);
        var usedTargetedFallback = burst.NeedsTargetedFallback;
        if (usedTargetedFallback)
        {
            frames.AddRange(Enumerable.Repeat<BitmapSource>(
                image,
                GtaLargeDigitReaderService.MaximumBurstFrames - frames.Count));
            burst = burstReader.Analyze(
                frames,
                ScannerDetectionMode.Fast,
                CancellationToken.None,
                burst.Analysis);
        }
        var analysis = burst.Analysis;
        var diagnostics = analysis.GtaResultDiagnostics;
        Console.WriteLine("$1,818,663 production regression output:");
        Console.WriteLine(
            $"  heading={diagnostics?.AnchorFound}; text={OneLine(diagnostics?.AnchorOcrText ?? string.Empty)}; " +
            $"amount={analysis.Candidate?.Amount}; decision={QuickCapturePolicy.Evaluate(analysis.Candidate, false)}; " +
            $"total={diagnostics?.Performance?.TotalScanMilliseconds:0.0} ms; passes={diagnostics?.Performance?.PreprocessingVariantsAttempted}; " +
            $"fallback={usedTargetedFallback}");
        if (diagnostics is not null)
        {
            foreach (var result in diagnostics.AmountOcrResults)
            {
                Console.WriteLine(
                    $"  {result.PreprocessingMethod} / {result.PageSegmentationMode} | " +
                    $"confidence={result.Confidence:P0} | raw={OneLine(result.RawText)} | " +
                    $"normalized={(string.IsNullOrWhiteSpace(result.NormalizedText) ? "--" : result.NormalizedText)} | " +
                    $"symbols={result.SymbolSummary}");
            }

            foreach (var digit in diagnostics.DigitOcrResults ?? [])
            {
                Console.WriteLine(
                    $"  digit {digit.Position}: box={digit.Bounds.X},{digit.Bounds.Y} " +
                    $"{digit.Bounds.Width}x{digit.Bounds.Height}; selected={digit.SelectedDigit}; " +
                    $"confidence={digit.Confidence:P0}; holes={digit.EnclosedRegionCount}; " +
                    $"uncertain={digit.IsUncertain}; {digit.OcrVotes}; {digit.ShapeSupport}");
            }
        }

        var amountBelowHeading = diagnostics?.AnchorBounds is Int32Rect heading &&
            diagnostics.AmountCropBounds.Y >= heading.Y + heading.Height;
        if (analysis.Candidate?.Amount != 1_818_663 ||
            analysis.Candidate.Amount == 1_816_663 ||
            diagnostics is null || !diagnostics.AnchorFound || !amountBelowHeading)
        {
            failures.Add("1818663 large translucent digit regression");
        }

        var passBudget = usedTargetedFallback ? 19 : 12;
        if (diagnostics?.Performance?.PreprocessingVariantsAttempted > passBudget)
        {
            failures.Add($"selected-region OCR pass budget exceeded {passBudget}");
        }
    }

    private static void RunReferenceQuickScanRegression(
        PayoutDetectionService detection,
        OcrService amountOcrService,
        ICollection<string> failures)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestSamples", "GtaTotalCashEarnedReference.png");
        if (!File.Exists(path))
        {
            failures.Add("reference Quick Scan screenshot asset missing");
            return;
        }

        var image = LoadImage(path);
        var frames = Enumerable.Repeat<BitmapSource>(
            image,
            GtaLargeDigitReaderService.MaximumBurstFrames).ToArray();
        var reader = new GtaLargeDigitReaderService(detection, amountOcrService);
        var result = reader.Analyze(
            frames,
            ScannerDetectionMode.Fast,
            CancellationToken.None,
            forceTargetedVerification: true);
        var diagnostics = result.Analysis.GtaResultDiagnostics;
        Console.WriteLine("$12,418,670 Quick Scan regression output:");
        Console.WriteLine(
            $"  amount={result.Analysis.Candidate?.Amount}; stable={result.StableFrameCount}; " +
            $"passes={diagnostics?.Performance?.PreprocessingVariantsAttempted}; " +
            $"duration={diagnostics?.Performance?.TotalScanMilliseconds:0.0} ms; " +
            $"competitors={diagnostics?.CompetingCandidates}");
        foreach (var amountResult in diagnostics?.AmountOcrResults ?? [])
        {
            Console.WriteLine(
                $"  {amountResult.PreprocessingMethod} / {amountResult.PageSegmentationMode}: " +
                $"raw={OneLine(amountResult.RawText)}; parsed={amountResult.ParsedAmount}; " +
                $"confidence={amountResult.Confidence:P0}; symbols={amountResult.SymbolSummary}");
        }

        foreach (var digit in diagnostics?.DigitOcrResults ?? [])
        {
            Console.WriteLine(
                $"  digit {digit.Position}: selected={digit.SelectedDigit}; confidence={digit.Confidence:P0}; " +
                $"uncertain={digit.IsUncertain}; box={digit.Bounds.X},{digit.Bounds.Y},{digit.Bounds.Width},{digit.Bounds.Height}");
        }

        if (result.Analysis.Candidate?.Amount != 12_418_670)
        {
            failures.Add("reference five-frame Quick Scan dropped a digit");
        }
    }

    private static void RunNormalizationTests(ICollection<string> failures)
    {
        var cases = new Dictionary<string, long>
        {
            ["$12,118,670"] = 12_118_670,
            ["12,118,670"] = 12_118_670,
            ["$12118670"] = 12_118_670,
            ["12118670"] = 12_118_670,
            ["$12 118 670"] = 12_118_670,
            ["$12.118.670"] = 12_118_670,
            ["$ 12,118,670"] = 12_118_670,
            ["$12,118,67O"] = 12_118_670
        };
        foreach (var item in cases)
        {
            if (!GtaResultScreenDetectionService.TryNormalizeAmountText(
                    item.Key,
                    out var normalized,
                    out var amount,
                    out _,
                    out _) ||
                amount != item.Value || normalized != "$12,118,670")
            {
                failures.Add($"normalization {item.Key}");
            }
        }

        foreach (var invalid in new[] { "-$20,000", "$0", "$$500,000", "$12,34,567", "$2,500,000,001", "ABC500" })
        {
            if (GtaResultScreenDetectionService.TryNormalizeAmountText(
                    invalid,
                    out _,
                    out _,
                    out _,
                    out _))
            {
                failures.Add($"invalid normalization {invalid}");
            }
        }
    }

    private static void RunUserPngRegressions(
        PayoutDetectionService detection,
        OcrService amountOcrService,
        ICollection<string> failures,
        string? requestedUserImage = null)
    {
        var cases = new[]
        {
            (FileName: "UserTest1.png", Expected: 968_000L),
            (FileName: "UserTest2.png", Expected: 1_413_716L),
            (FileName: "UserTest3.png", Expected: 968_000L),
            (FileName: "UserTest4.png", Expected: 2_538_000L),
            (FileName: "UserTest5.png", Expected: 3_615_920L)
        };
        var reader = new GtaLargeDigitReaderService(detection, amountOcrService);
        foreach (var testCase in cases.Where(testCase =>
                     string.IsNullOrWhiteSpace(requestedUserImage) ||
                     testCase.FileName.Contains(requestedUserImage, StringComparison.OrdinalIgnoreCase)))
        {
            var path = Path.Combine(AppContext.BaseDirectory, "TestSamples", testCase.FileName);
            if (!File.Exists(path))
            {
                failures.Add($"{testCase.FileName} missing");
                continue;
            }

            var image = LoadImage(path);
            var frames = Enumerable.Repeat<BitmapSource>(
                image,
                GtaLargeDigitReaderService.MaximumBurstFrames).ToArray();
            var result = reader.Analyze(
                frames,
                ScannerDetectionMode.Fast,
                CancellationToken.None,
                forceTargetedVerification: true);
            var diagnostics = result.Analysis.GtaResultDiagnostics;
            Console.WriteLine(
                $"{testCase.FileName}: expected={testCase.Expected}; actual={result.Analysis.Candidate?.Amount}; " +
                $"anchor={diagnostics?.AnchorFound}; readers={diagnostics?.IndependentReaderAgreement}; " +
                $"competitors={diagnostics?.CompetingCandidates}; passes={diagnostics?.Performance?.PreprocessingVariantsAttempted}; " +
                $"duration={diagnostics?.Performance?.TotalScanMilliseconds:0.0} ms");
            if (result.Analysis.Candidate?.Amount != testCase.Expected)
            {
                failures.Add($"{testCase.FileName} payout regression");
                var fullFallback = detection.Analyze(image, CancellationToken.None);
                Console.WriteLine(
                    $"  full optimized fallback: amount={fullFallback.Candidate?.Amount}; " +
                    $"method={fullFallback.Candidate?.PreprocessingMethod}; " +
                    $"competitors={fullFallback.GtaResultDiagnostics?.CompetingCandidates}");
                foreach (var amountResult in fullFallback.GtaResultDiagnostics?.AmountOcrResults ?? [])
                {
                    Console.WriteLine(
                        $"    full {amountResult.PreprocessingMethod} / {amountResult.PageSegmentationMode}: " +
                        $"raw={OneLine(amountResult.RawText)}; parsed={amountResult.ParsedAmount}; " +
                        $"confidence={amountResult.Confidence:P0}; symbols={amountResult.SymbolSummary}");
                }
                foreach (var amountResult in diagnostics?.AmountOcrResults ?? [])
                {
                    Console.WriteLine(
                        $"  {amountResult.PreprocessingMethod} / {amountResult.PageSegmentationMode}: " +
                        $"raw={OneLine(amountResult.RawText)}; parsed={amountResult.ParsedAmount}; " +
                        $"confidence={amountResult.Confidence:P0}; symbols={amountResult.SymbolSummary}");
                }
            }
        }
    }

    private static void RunPolicyAndDuplicateTests(ICollection<string> failures)
    {
        var uncertain = new DetectedPayout(
            500_000,
            "TOTAL CASH EARNED\n$500,000",
            "TOTAL CASH EARNED | $500,000",
            PayoutDetectionMethod.GtaTotalCashEarnedResultScreen,
            "Generated uncertain",
            0.9f,
            "uncertain",
            AutomaticAddRecommended: false);
        if (QuickCapturePolicy.Evaluate(uncertain, false) != QuickCaptureDecision.RequireConfirmation)
        {
            failures.Add("uncertain confirmation fallback");
        }

        var lowConfidence = uncertain with
        {
            Confidence = 0.29f,
            AutomaticAddRecommended = true,
            ImageFingerprint = "low-confidence"
        };
        if (QuickCapturePolicy.Evaluate(lowConfidence, false) != QuickCaptureDecision.RequireConfirmation)
        {
            failures.Add("29 percent confidence never auto-adds");
        }

        var duplicateCandidate = uncertain with
        {
            ImageFingerprint = "same-visible-image",
            AutomaticAddRecommended = true
        };
        var duplicateGuard = new PayoutDuplicateGuard();
        var now = DateTime.UtcNow;
        var firstPresentation = duplicateGuard.ShouldPresent(duplicateCandidate, now);
        duplicateGuard.RecordDismissal(duplicateCandidate, now);
        var immediateRepeat = duplicateGuard.ShouldPresent(duplicateCandidate, now.AddSeconds(1));
        duplicateGuard.RecordNoCandidate("missing-1");
        duplicateGuard.RecordNoCandidate("missing-2");
        var laterPresentation = duplicateGuard.ShouldPresent(duplicateCandidate, now.AddSeconds(5));
        if (!firstPresentation || immediateRepeat || !laterPresentation)
        {
            failures.Add("duplicate lifecycle");
        }
    }

    private static void RunSettingsCompatibilityTest(ICollection<string> failures)
    {
        var legacySettings = JsonSerializer.Deserialize<CaptureSettings>(
            "{\"X\":10,\"Y\":20,\"Width\":300,\"Height\":100}");
        if (legacySettings is null ||
            !legacySettings.HotkeyEnabled ||
            legacySettings.HotkeyDisplayText != "Ctrl + Shift + H" ||
            !legacySettings.AlwaysOnTop ||
            !legacySettings.PauseLivePreviewWhenHidden ||
            !legacySettings.ContinueScannerWhileHidden ||
            !legacySettings.ShowTrayNotificationAfterPayout ||
            legacySettings.DetectionMode != ScannerDetectionMode.Fast ||
            legacySettings.AreaMode != ScannerAreaMode.SelectedRegion ||
            legacySettings.CaptureAreaConfirmed ||
            !legacySettings.RequireQuickCaptureConfirmation ||
            legacySettings.QuickCaptureMonitorId != "Primary")
        {
            failures.Add("legacy settings defaults");
        }


        var legacyAutoLocate = new CaptureSettings
        {
            X = 10,
            Y = 20,
            Width = 300,
            Height = 100,
            AreaMode = ScannerAreaMode.AutoLocate
        };
        var migrated = CaptureSettingsService.Migrate(legacyAutoLocate);
        if (migrated.AreaMode != ScannerAreaMode.SelectedRegion ||
            migrated.X != 10 || migrated.Y != 20 || migrated.Width != 300 || migrated.Height != 100 ||
            migrated.CaptureAreaConfirmed || !migrated.RequireQuickCaptureConfirmation)
        {
            failures.Add("legacy capture settings require a new confirmed selection");
        }

        var confirmed = CaptureSettingsService.Migrate(new CaptureSettings
        {
            X = 25,
            Y = 30,
            Width = 500,
            Height = 180,
            CaptureAreaConfirmed = true
        });
        if (!confirmed.HasConfirmedCaptureArea)
        {
            failures.Add("confirmed selected region survives restart migration");
        }
    }

    private static void RunSelectedRegionSafetyTests(ICollection<string> failures)
    {
        var captureService = new ScreenCaptureService();
        var virtualBounds = captureService.GetVirtualScreenBounds();
        var selected = new CaptureSettings
        {
            X = virtualBounds.X,
            Y = virtualBounds.Y,
            Width = Math.Min(500, virtualBounds.Width),
            Height = Math.Min(200, virtualBounds.Height),
            AreaMode = ScannerAreaMode.SelectedRegion
        };
        if (!captureService.IsValidCaptureArea(selected) ||
            selected.Width == virtualBounds.Width && selected.Height == virtualBounds.Height)
        {
            failures.Add("selected-region-only capture bounds");
        }
    }

    private static void RunConsensusTests(ICollection<string> failures)
    {
        var threeToOne = GtaLargeDigitReaderService.EvaluateConsensus(
            [
                new PayoutReaderVote("frame-1", 1_818_663, 0.80f),
                new PayoutReaderVote("frame-2", 1_818_663, 0.75f),
                new PayoutReaderVote("median", 1_818_663, 0.82f),
                new PayoutReaderVote("bright", 1_816_663, 0.79f)
            ],
            segmentedDigitCount: 7);
        if (threeToOne.WinningAmount != 1_818_663 || !threeToOne.MeetsExactAgreementPolicy)
        {
            failures.Add("three readers beat one disagreement");
        }

        var tied = GtaLargeDigitReaderService.EvaluateConsensus(
            [
                new PayoutReaderVote("frame-1", 1_818_663, 0.80f),
                new PayoutReaderVote("frame-2", 1_818_663, 0.75f),
                new PayoutReaderVote("median", 1_816_663, 0.82f),
                new PayoutReaderVote("bright", 1_816_663, 0.79f)
            ],
            segmentedDigitCount: 7);
        if (!tied.HasEquallySupportedConflict || tied.MeetsExactAgreementPolicy)
        {
            failures.Add("equally supported amounts require confirmation");
        }

        var missingDigit = GtaLargeDigitReaderService.EvaluateConsensus(
            [
                new PayoutReaderVote("frame-1", 818_663, 0.80f),
                new PayoutReaderVote("frame-2", 818_663, 0.75f),
                new PayoutReaderVote("median", 818_663, 0.82f)
            ],
            segmentedDigitCount: 7);
        if (!missingDigit.HasDigitCountConflict || missingDigit.MeetsExactAgreementPolicy)
        {
            failures.Add("segmented digit count conflict");
        }
    }

    private static void RunAccuracyFirstBurstTest(
        PayoutDetectionService detection,
        OcrService amountOcrService,
        ICollection<string> failures)
    {
        var image = RenderResultImage(["TOTAL CASH EARNED", "$1,818,663"]);
        var frames = Enumerable.Repeat<BitmapSource>(
            image,
            GtaLargeDigitReaderService.MaximumBurstFrames).ToArray();
        var reader = new GtaLargeDigitReaderService(detection, amountOcrService);
        var result = reader.Analyze(
            frames,
            ScannerDetectionMode.Fast,
            CancellationToken.None,
            forceTargetedVerification: true);
        var passes = result.Analysis.GtaResultDiagnostics?.Performance?.PreprocessingVariantsAttempted;
        Console.WriteLine(
            $"accuracy-first Quick Scan: amount={result.Analysis.Candidate?.Amount}; " +
            $"frames={result.CapturedFrameCount}; stable={result.StableFrameCount}; " +
            $"passes={passes}; duration={result.Analysis.GtaResultDiagnostics?.Performance?.TotalScanMilliseconds:0.0} ms");
        if (result.Analysis.Candidate?.Amount != 1_818_663 ||
            result.CapturedFrameCount != GtaLargeDigitReaderService.MaximumBurstFrames ||
            result.NeedsTargetedFallback ||
            passes is null or > 26)
        {
            failures.Add("accuracy-first five-frame Quick Scan");
        }

        var windowConstants = typeof(GTAEarningsTracker.MainWindow);
        var compactNormalHeight = Convert.ToDouble(
            windowConstants.GetField(nameof(GTAEarningsTracker.MainWindow.CompactNormalHeight))?.GetRawConstantValue(),
            CultureInfo.InvariantCulture);
        var compactPendingHeight = Convert.ToDouble(
            windowConstants.GetField(nameof(GTAEarningsTracker.MainWindow.CompactPendingHeight))?.GetRawConstantValue(),
            CultureInfo.InvariantCulture);
        var compactWidth = Convert.ToDouble(
            windowConstants.GetField(nameof(GTAEarningsTracker.MainWindow.CompactWindowWidth))?.GetRawConstantValue(),
            CultureInfo.InvariantCulture);
        if (compactNormalHeight < 320 ||
            compactPendingHeight <= compactNormalHeight ||
            compactWidth < 400)
        {
            failures.Add("compact layout reserves visible scan and confirmation space");
        }
    }

    private static void RunFrameChangeTests(ICollection<string> failures)
    {
        var detector = new FrameChangeDetector();
        var firstFrame = RenderResultImage(["TOTAL CASH EARNED", "$500,000"]);
        var changedFrame = RenderResultImage(["TOTAL CASH EARNED", "$1,250,000"]);
        var start = DateTime.UtcNow;
        var initial = detector.Observe(firstFrame, start);
        var stabilizing = detector.Observe(firstFrame, start.AddMilliseconds(100));
        var firstReady = detector.Observe(firstFrame, start.AddMilliseconds(250));
        var unchanged = detector.Observe(firstFrame, start.AddSeconds(1));
        var changed = detector.Observe(changedFrame, start.AddSeconds(2));
        var secondReady = detector.Observe(changedFrame, start.AddSeconds(2).AddMilliseconds(250));
        if (initial.Decision != FrameProcessingDecision.ChangeDetected ||
            stabilizing.Decision != FrameProcessingDecision.Stabilizing ||
            firstReady.Decision != FrameProcessingDecision.ReadyForOcr ||
            unchanged.Decision != FrameProcessingDecision.IgnoredUnchanged ||
            changed.Decision != FrameProcessingDecision.ChangeDetected ||
            secondReady.Decision != FrameProcessingDecision.ReadyForOcr)
        {
            failures.Add("stable frame change gate");
        }

        var queueCapacity = (int)(typeof(BackgroundScannerService)
            .GetField(nameof(BackgroundScannerService.LatestFrameQueueCapacity))?
            .GetRawConstantValue() ?? 0);
        if (queueCapacity != 1)
        {
            failures.Add("latest frame queue bounded capacity");
        }
    }

    private static DetectedPayout? GetSpecializedCandidate(PayoutAnalysisResult analysis)
    {
        return analysis.Candidate?.DetectionMethod ==
            PayoutDetectionMethod.GtaTotalCashEarnedResultScreen
                ? analysis.Candidate
                : null;
    }

    private static BitmapSource RenderResultImage(IReadOnlyList<string> lines)
    {
        const int width = 1200;
        const int height = 420;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(15, 42, 17)), null, new Rect(0, 0, width, height));
            for (var x = 0; x < width; x += 80)
            {
                drawing.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(35, 180, 210, 180)), 1), new Point(x, 0), new Point(x - 100, height));
            }

            var typeface = new Typeface(
                new FontFamily("Arial Narrow"),
                FontStyles.Normal,
                FontWeights.Bold,
                FontStretches.Condensed);
            var y = lines.Count == 1 ? 155d : 48d;
            for (var index = 0; index < lines.Count; index++)
            {
                var isHeading = index == 0 && lines.Count > 1;
                var fontSize = isHeading ? 62 : lines.Count > 2 ? 90 : 150;
                var text = new FormattedText(
                    lines[index],
                    CultureInfo.GetCultureInfo("en-US"),
                    FlowDirection.LeftToRight,
                    typeface,
                    fontSize,
                    new SolidColorBrush(Color.FromArgb(isHeading ? (byte)245 : (byte)215, 238, 238, 238)),
                    1.0);
                drawing.DrawText(text, new Point(42, y));
                y += isHeading ? 105 : lines.Count > 2 ? 108 : 170;
            }
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource LoadImage(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    private static string OneLine(string text)
    {
        return string.IsNullOrWhiteSpace(text)
            ? "(empty)"
            : string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
