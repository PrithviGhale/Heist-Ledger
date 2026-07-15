using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;
using GTAEarningsTracker.Models;

namespace GTAEarningsTracker.Services;

public sealed class OptimizedGtaResultScreenDetectionService
{
    public const float MinimumAnchorConfidence = 0.10f;
    public const float ExceptionalAmountConfidence = 0.92f;
    public const float MinimumFastAmountConfidence = 0.60f;
    public const double CompetingCandidateMargin = 8;
    public const int MinimumAgreeingMethods = 3;
    public const double RepeatedDigitSimilarityThreshold = 18;

    private static readonly Regex AnchorWordRegex = new(
        "[A-Z0-9]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NegativeAmountRegex = new(
        @"-\s*\$?\s*\d",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly OcrService _headingOcr;
    private readonly OcrService _amountOcr;
    private readonly FastGtaPreprocessingService _preprocessing = new();

    public OptimizedGtaResultScreenDetectionService(
        OcrService headingOcr,
        OcrService amountOcr)
    {
        _headingOcr = headingOcr;
        _amountOcr = amountOcr;
    }

    public GtaResultScreenDetectionResult Detect(
        BitmapSource capture,
        string imageFingerprint,
        ScannerDetectionMode mode,
        CancellationToken cancellationToken,
        bool selectedRegionProbe = false)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var totalStopwatch = Stopwatch.StartNew();
        var headingPreprocessingMilliseconds = 0d;
        var headingOcrMilliseconds = 0d;
        var amountCropMilliseconds = 0d;
        var amountPreprocessingMilliseconds = 0d;
        var amountOcrMilliseconds = 0d;
        var fallbackMilliseconds = 0d;
        var variantsAttempted = 0;
        var path = "Fast rejection";

        // A selected rectangle is user-controlled and may include generous space above the
        // GTA result (especially at ultrawide resolutions). The anchor is conservative enough
        // to search the complete saved rectangle without accepting unrelated numbers.
        var headingBounds = RelativeBounds(capture, 0, 0, 1, 1);
        var headingCrop = Crop(capture, headingBounds);
        var headingBatch = _preprocessing.CreateHeadingVariants(headingCrop);
        headingPreprocessingMilliseconds = headingBatch.DurationMilliseconds;
        AnchorResult? bestAnchor = null;
        var headingAttempts = new List<OcrRecognitionResult>();
        foreach (var variant in headingBatch.Images)
        {
            cancellationToken.ThrowIfCancellationRequested();
            variantsAttempted++;
            var stopwatch = Stopwatch.StartNew();
            var recognition = _headingOcr.RecognizeHeading(variant, cancellationToken);
            headingAttempts.Add(recognition);
            stopwatch.Stop();
            headingOcrMilliseconds += stopwatch.Elapsed.TotalMilliseconds;
            var anchor = FindAnchor(recognition, variant.Scale, headingBounds);
            if (anchor is not null && (bestAnchor is null || anchor.Confidence > bestAnchor.Confidence))
            {
                bestAnchor = anchor;
            }

            if (anchor is { IsExactMatch: true, Confidence: >= MinimumAnchorConfidence })
            {
                break;
            }
        }

        var amountBounds = CreateAmountBounds(capture, bestAnchor?.Bounds);
        if (bestAnchor is null || bestAnchor.Confidence < MinimumAnchorConfidence)
        {
            return Complete(
                null,
                "The ordered TOTAL CASH EARNED anchor was not detected confidently.",
                [],
                null,
                0,
                string.Empty,
                string.Empty,
                0,
                "None",
                false,
                null,
                null);
        }

        var cropStopwatch = Stopwatch.StartNew();
        var amountCrop = Crop(capture, amountBounds);
        cropStopwatch.Stop();
        amountCropMilliseconds = cropStopwatch.Elapsed.TotalMilliseconds;
        var diagnostics = new List<GtaAmountOcrDiagnostic>();
        var candidates = new List<AmountResult>();

        var safetyBounds = new Int32Rect(
            (int)Math.Floor(capture.PixelWidth * 0.02),
            amountBounds.Y,
            Math.Max(1, (int)Math.Ceiling(capture.PixelWidth * 0.98) - (int)Math.Floor(capture.PixelWidth * 0.02)),
            amountBounds.Height);
        var safetyCrop = Crop(capture, safetyBounds);
        var safetyVariant = _preprocessing.CreateSignedSafetyVariant(safetyCrop);
        var safetyStopwatch = Stopwatch.StartNew();
        var safetyRecognition = _amountOcr.Recognize(safetyVariant, cancellationToken);
        safetyStopwatch.Stop();
        amountOcrMilliseconds += safetyStopwatch.Elapsed.TotalMilliseconds;
        variantsAttempted++;
        var safetyValid = GtaResultScreenDetectionService.TryNormalizeAmountText(
            safetyRecognition.Text,
            out var safetyNormalized,
            out var safetyAmount,
            out var safetyHadDollar,
            out var safetyGrouping);
        diagnostics.Add(new GtaAmountOcrDiagnostic(
            safetyVariant.Method,
            "SingleLine safety",
            safetyRecognition.Text,
            safetyValid ? safetyNormalized : string.Empty,
            QuickCapturePolicy.NormalizeConfidence(safetyRecognition.Confidence),
            safetyValid ? safetyAmount : null,
            FormatSymbols(safetyRecognition.Symbols)));
        if (NegativeAmountRegex.IsMatch(safetyRecognition.Text))
        {
            return Complete(
                null,
                "A negative amount marker was detected below the anchor.",
                diagnostics,
                amountCrop,
                0,
                "Signed safety pass",
                "SingleLine",
                0,
                "None",
                false,
                null,
                null);
        }

        if (safetyValid && safetyAmount.ToString(CultureInfo.InvariantCulture).Length >= 4)
        {
            candidates.Add(new AmountResult(
                safetyAmount,
                safetyRecognition.Text,
                safetyNormalized,
                QuickCapturePolicy.NormalizeConfidence(safetyRecognition.Confidence),
                safetyVariant.Method,
                "SingleLine safety",
                safetyHadDollar,
                safetyGrouping,
                false,
                safetyVariant,
                safetyRecognition.Symbols));
        }

        var fastBatch = _preprocessing.CreateFastAmountVariants(amountCrop);
        amountPreprocessingMilliseconds += fastBatch.DurationMilliseconds;
        foreach (var variant in fastBatch.Images)
        {
            ProcessAmountVariant(variant, isFallback: false);
        }

        var groups = ScoreCandidates(candidates, bestAnchor.Confidence);
        var fastDecision = Evaluate(groups);
        if (fastDecision.IsHighConfidence)
        {
            path = "Fast path";
            return CompleteCandidate(fastDecision, fallbackUsed: false);
        }

        if (fastDecision.HasEquallyPlausibleConflict &&
            fastDecision.Winner is { Score: >= 55, Best.Confidence: >= 0.45f })
        {
            // Conflict handling is deliberately not bypassed by extra OCR passes.
            path = "Fast path conflict";
            return CompleteCandidate(fastDecision, fallbackUsed: false);
        }

        if (selectedRegionProbe && groups.Count > 0)
        {
            path = "Selected-region fast probe";
            return CompleteCandidate(fastDecision, fallbackUsed: false);
        }

        path = "Fallback";
        var fallbackStopwatch = Stopwatch.StartNew();
        var fallbackBatch = _preprocessing.CreateFallbackVariants(amountCrop, mode);
        amountPreprocessingMilliseconds += fallbackBatch.DurationMilliseconds;
        foreach (var variant in fallbackBatch.Images)
        {
            ProcessAmountVariant(variant, isFallback: true);
            var partialDecision = Evaluate(ScoreCandidates(candidates, bestAnchor.Confidence));
            if (partialDecision.IsHighConfidence &&
                candidates.Where(candidate => candidate.Confidence >= 0.25f)
                    .Select(candidate => GetEvidenceFamily(candidate.Method))
                    .Distinct()
                    .Count() >= 3)
            {
                break;
            }
        }

        fallbackStopwatch.Stop();
        fallbackMilliseconds = fallbackStopwatch.Elapsed.TotalMilliseconds;
        if (mode == ScannerDetectionMode.Thorough)
        {
            AddCharacterVerificationCandidate();
        }

        groups = ScoreCandidates(candidates, bestAnchor.Confidence);
        var finalDecision = Evaluate(groups);
        if (groups.Count == 0)
        {
            return Complete(
                null,
                "The anchor was found, but no valid amount was recognized below it.",
                diagnostics,
                amountCrop,
                0,
                "None",
                "None",
                0,
                "None",
                false,
                null,
                null);
        }

        return CompleteCandidate(finalDecision, fallbackUsed: true);

        void ProcessAmountVariant(PreprocessedImage variant, bool isFallback)
        {
            foreach (var segmentationMode in Enum.GetValues<OcrAmountPageSegmentation>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                variantsAttempted++;
                var stopwatch = Stopwatch.StartNew();
                var recognition = _amountOcr.RecognizeAmount(variant, segmentationMode, cancellationToken);
                stopwatch.Stop();
                amountOcrMilliseconds += stopwatch.Elapsed.TotalMilliseconds;
                var valid = GtaResultScreenDetectionService.TryNormalizeAmountText(
                    recognition.Text,
                    out var normalized,
                    out var amount,
                    out var hadDollar,
                    out var validGrouping);
                var confidence = QuickCapturePolicy.NormalizeConfidence(recognition.Confidence);
                diagnostics.Add(new GtaAmountOcrDiagnostic(
                    variant.Method,
                    segmentationMode.ToString(),
                    recognition.Text,
                    normalized,
                    confidence,
                    valid ? amount : null,
                    FormatSymbols(recognition.Symbols)));
                if (!valid ||
                    amount.ToString(CultureInfo.InvariantCulture).Length < 4 ||
                    !HasPlausibleLineGeometry(recognition.TextLines, variant.Scale, amountBounds.Width))
                {
                    continue;
                }

                candidates.Add(new AmountResult(
                    amount,
                    recognition.Text,
                    normalized,
                    confidence,
                    variant.Method,
                    segmentationMode.ToString(),
                    hadDollar,
                    validGrouping,
                    isFallback,
                    variant,
                    recognition.Symbols));
            }
        }

        void AddCharacterVerificationCandidate()
        {
            var sourceCandidate = candidates
                .Where(candidate => candidate.IsFallback &&
                    candidate.Symbols is { Count: > 0 } &&
                    candidate.RawText.Count(char.IsDigit) == candidate.Symbols!.Count(symbol => symbol.Text.All(char.IsDigit)))
                .OrderByDescending(candidate => candidate.Confidence)
                .FirstOrDefault();
            if (sourceCandidate?.Symbols is not { Count: > 0 } symbols)
            {
                return;
            }

            var processedImage = GtaAmountPreprocessingService.DecodePreview(sourceCandidate.Variant);
            var repeatedDigitText = TryCorrectRepeatedDigit(
                sourceCandidate.RawText,
                symbols,
                processedImage,
                out var repeatedDigitEvidence);
            var repeatedValid = GtaResultScreenDetectionService.TryNormalizeAmountText(
                repeatedDigitText,
                out var repeatedNormalized,
                out var repeatedAmount,
                out var repeatedDollar,
                out var repeatedGrouping);
            diagnostics.Add(new GtaAmountOcrDiagnostic(
                $"{sourceCandidate.Method} repeated-digit verification",
                "Visual cell comparison",
                repeatedDigitEvidence,
                repeatedNormalized,
                sourceCandidate.Confidence,
                repeatedValid ? repeatedAmount : null));
            if (repeatedValid && repeatedDigitText != sourceCandidate.RawText)
            {
                candidates.Add(new AmountResult(
                    repeatedAmount,
                    repeatedDigitText,
                    repeatedNormalized,
                    sourceCandidate.Confidence,
                    "Repeated digit visual verification",
                    "Visual cell comparison",
                    repeatedDollar,
                    repeatedGrouping,
                    true,
                    sourceCandidate.Variant,
                    symbols));
            }

            var verifiedDigits = new List<char>();
            var confidences = new List<float>();
            foreach (var symbol in symbols.Where(symbol => symbol.Text.All(char.IsDigit)))
            {
                var paddingX = Math.Max(2, (symbol.Right - symbol.Left) / 8);
                var paddingY = Math.Max(2, (symbol.Bottom - symbol.Top) / 10);
                var bounds = new Int32Rect(
                    Math.Max(0, symbol.Left - paddingX),
                    Math.Max(0, symbol.Top - paddingY),
                    Math.Min(processedImage.PixelWidth, symbol.Right + paddingX) - Math.Max(0, symbol.Left - paddingX),
                    Math.Min(processedImage.PixelHeight, symbol.Bottom + paddingY) - Math.Max(0, symbol.Top - paddingY));
                var characterVariant = _preprocessing.CreateCharacterVariant(
                    processedImage,
                    bounds,
                    $"Character verification {verifiedDigits.Count + 1}");
                variantsAttempted++;
                var stopwatch = Stopwatch.StartNew();
                var recognition = _amountOcr.RecognizeAmountCharacter(characterVariant, cancellationToken);
                stopwatch.Stop();
                amountOcrMilliseconds += stopwatch.Elapsed.TotalMilliseconds;
                var recognizedDigit = recognition.Text.FirstOrDefault(char.IsDigit);
                if (recognizedDigit == default)
                {
                    verifiedDigits.Add('?');
                    confidences.Add(0);
                    continue;
                }

                verifiedDigits.Add(recognizedDigit);
                confidences.Add(QuickCapturePolicy.NormalizeConfidence(recognition.Confidence));
            }

            var verifiedText = new string(verifiedDigits.ToArray());
            var averageConfidence = confidences.Count == 0 ? 0 : confidences.Average();
            var valid = GtaResultScreenDetectionService.TryNormalizeAmountText(
                verifiedText,
                out var normalized,
                out var amount,
                out var hadDollar,
                out var grouping);
            diagnostics.Add(new GtaAmountOcrDiagnostic(
                $"{sourceCandidate.Method} character verification",
                "SingleChar",
                verifiedText,
                normalized,
                averageConfidence,
                valid ? amount : null,
                string.Join(" ", symbols.Select(symbol => $"{symbol.Text}:{symbol.Right - symbol.Left}x{symbol.Bottom - symbol.Top}"))));
            if (!valid)
            {
                return;
            }

            candidates.Add(new AmountResult(
                amount,
                verifiedText,
                normalized,
                averageConfidence,
                "Character verification",
                "SingleChar",
                hadDollar,
                grouping,
                true,
                sourceCandidate.Variant,
                symbols));
        }

        GtaResultScreenDetectionResult CompleteCandidate(CandidateDecision decision, bool fallbackUsed)
        {
            var winner = decision.Winner!;
            var selected = winner.Best;
            var automatic = decision.IsHighConfidence && !decision.HasEquallyPlausibleConflict;
            var reason = automatic
                ? $"Accepted: {winner.AgreeingMethods} independent preprocessing methods agreed with no similarly plausible amount."
                : decision.HasEquallyPlausibleConflict
                    ? "Confirmation required: materially different amount candidates remain similarly plausible."
                    : "Confirmation required: the amount was plausible but independent OCR agreement was insufficient.";
            var candidate = new DetectedPayout(
                selected.Amount,
                $"{bestAnchor.RawText}\n{selected.RawText}".Trim(),
                $"{bestAnchor.NormalizedText} | {selected.NormalizedText}",
                PayoutDetectionMethod.GtaTotalCashEarnedResultScreen,
                selected.Method,
                (float)Math.Clamp(winner.Score / 100, 0, 1),
                imageFingerprint,
                decision.HasEquallyPlausibleConflict,
                automatic);
            return Complete(
                candidate,
                reason,
                diagnostics,
                amountCrop,
                selected.Confidence,
                selected.Method,
                selected.SegmentationMode,
                winner.AgreeingMethods,
                FormatCompetitors(decision.Groups),
                automatic,
                selected,
                fallbackUsed ? "Fallback" : "Fast path");
        }

        GtaResultScreenDetectionResult Complete(
            DetectedPayout? candidate,
            string reason,
            IReadOnlyList<GtaAmountOcrDiagnostic> amountDiagnostics,
            BitmapSource? amountPreview,
            float amountConfidence,
            string selectedMethod,
            string selectedPsm,
            int agreement,
            string competitors,
            bool automatic,
            AmountResult? selected,
            string? forcedPath)
        {
            totalStopwatch.Stop();
            var finalPath = forcedPath ?? path;
            var performance = new ScannerPerformanceDiagnostics(
                "Direct image",
                0,
                0,
                0,
                0,
                1,
                0,
                0,
                headingPreprocessingMilliseconds,
                headingOcrMilliseconds,
                amountCropMilliseconds,
                amountPreprocessingMilliseconds,
                amountOcrMilliseconds,
                fallbackMilliseconds,
                totalStopwatch.Elapsed.TotalMilliseconds,
                finalPath,
                variantsAttempted,
                candidate is null ? reason : string.Empty,
                candidate is null ? null : DateTime.UtcNow);
#if DEBUG
            BitmapSource? headingPreview = headingCrop;
            BitmapSource? finalAmountPreview = amountPreview;
#else
            BitmapSource? headingPreview = null;
            BitmapSource? finalAmountPreview = null;
#endif
            var resultDiagnostics = new GtaResultScreenDiagnostics(
                bestAnchor is not null,
                bestAnchor?.RawText ?? string.Join(" | ", headingAttempts.Select(attempt => attempt.Text)),
                bestAnchor?.Confidence ?? headingAttempts.Select(attempt => QuickCapturePolicy.NormalizeConfidence(attempt.Confidence)).DefaultIfEmpty().Max(),
                bestAnchor?.Bounds,
                amountBounds,
                selected?.Amount,
                amountConfidence,
                selectedMethod,
                selectedPsm,
                agreement,
                competitors,
                automatic,
                reason,
                amountDiagnostics,
                headingPreview,
                finalAmountPreview,
                null,
                performance);
            return new GtaResultScreenDetectionResult(candidate, resultDiagnostics);
        }
    }

    private static IReadOnlyList<ScoredGroup> ScoreCandidates(
        IReadOnlyList<AmountResult> candidates,
        float anchorConfidence)
    {
        return candidates
            .GroupBy(candidate => candidate.Amount)
            .Select(group =>
            {
                var values = group.ToList();
                var best = values.OrderByDescending(value => value.Confidence).First();
                var methods = values
                    .GroupBy(value => GetEvidenceFamily(value.Method))
                    .Count(group => group.Max(value => value.Confidence) >= 0.25f);
                var digitCount = best.Amount.ToString(CultureInfo.InvariantCulture).Length;
                var score = (anchorConfidence * 20) +
                    (best.Confidence * 35) +
                    Math.Min(28, Math.Max(0, methods - 1) * 8) +
                    (best.HadDollar ? 5 : 0) +
                    (best.ValidGrouping ? 5 : 0) +
                    (digitCount is >= 4 and <= 10 ? 5 : 0) +
                    5;
                return new ScoredGroup(best.Amount, best, methods, score);
            })
            .OrderByDescending(group => group.Score)
            .ToList();
    }

    private static CandidateDecision Evaluate(IReadOnlyList<ScoredGroup> groups)
    {
        if (groups.Count == 0)
        {
            return new CandidateDecision(null, groups, false, false);
        }

        var winner = groups[0];
        var conflict = groups.Count > 1 && winner.Score - groups[1].Score < CompetingCandidateMargin;
        var highConfidence = !conflict &&
            winner.AgreeingMethods >= MinimumAgreeingMethods &&
            winner.Best.Confidence >= MinimumFastAmountConfidence &&
            winner.Score >= 55;
        return new CandidateDecision(winner, groups, highConfidence, conflict);
    }

    private static AnchorResult? FindAnchor(
        OcrRecognitionResult recognition,
        double scale,
        Int32Rect headingBounds)
    {
        var lines = recognition.TextLines is { Count: > 0 }
            ? recognition.TextLines
            : [new OcrTextLine(recognition.Text, recognition.Confidence, null, null)];
        foreach (var line in lines)
        {
            if (!TryMatchAnchor(line.Text, out var exact))
            {
                continue;
            }

            Int32Rect? bounds = null;
            if (line.Left is int left && line.Right is int right &&
                line.Top is int top && line.Bottom is int bottom)
            {
                bounds = new Int32Rect(
                    headingBounds.X + (int)Math.Floor(left / scale),
                    headingBounds.Y + (int)Math.Floor(top / scale),
                    Math.Max(1, (int)Math.Ceiling((right - left) / scale)),
                    Math.Max(1, (int)Math.Ceiling((bottom - top) / scale)));
            }

            return new AnchorResult(
                line.Text.Trim(),
                NormalizeAnchor(line.Text),
                Math.Max(
                    QuickCapturePolicy.NormalizeConfidence(line.Confidence),
                    QuickCapturePolicy.NormalizeConfidence(recognition.Confidence)),
                bounds,
                exact);
        }

        if (TryMatchAnchor(recognition.Text, out var fullExact))
        {
            return new AnchorResult(
                recognition.Text.Trim(),
                NormalizeAnchor(recognition.Text),
                QuickCapturePolicy.NormalizeConfidence(recognition.Confidence),
                null,
                fullExact);
        }

        return null;
    }

    private static bool TryMatchAnchor(string text, out bool exact)
    {
        exact = false;
        var words = AnchorWordRegex.Matches(text.ToUpperInvariant())
            .Select(match => match.Value)
            .ToList();
        for (var index = 0; index < words.Count; index++)
        {
            if (index + 2 < words.Count &&
                Close(words[index], "TOTAL") && Close(words[index + 1], "CASH") && Close(words[index + 2], "EARNED"))
            {
                exact = words[index] == "TOTAL" && words[index + 1] == "CASH" && words[index + 2] == "EARNED";
                return true;
            }

            if (index + 1 < words.Count &&
                Close(words[index], "TOTALCASH") && Close(words[index + 1], "EARNED"))
            {
                exact = words[index] == "TOTALCASH" && words[index + 1] == "EARNED";
                return true;
            }

            if (Close(words[index], "TOTALCASHEARNED"))
            {
                exact = words[index] == "TOTALCASHEARNED";
                return true;
            }
        }

        return false;
    }

    private static string NormalizeAnchor(string text)
    {
        return string.Join(" ", AnchorWordRegex.Matches(text.ToUpperInvariant()).Select(match => match.Value));
    }

    private static bool Close(string candidate, string expected)
    {
        if (candidate == expected)
        {
            return true;
        }

        if (Math.Abs(candidate.Length - expected.Length) > 1)
        {
            return false;
        }

        var previous = Enumerable.Range(0, expected.Length + 1).ToArray();
        var current = new int[expected.Length + 1];
        for (var i = 1; i <= candidate.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= expected.Length; j++)
            {
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (candidate[i - 1] == expected[j - 1] ? 0 : 1));
            }

            (previous, current) = (current, previous);
        }

        return previous[expected.Length] <= 1;
    }

    private static bool HasPlausibleLineGeometry(
        IReadOnlyList<OcrTextLine>? lines,
        double scale,
        int cropWidth)
    {
        if (lines is not { Count: > 0 })
        {
            return true;
        }

        return lines.Any(line =>
            line.Left is int left && line.Right is int right &&
            line.Bottom is int bottom && line.Top is int top &&
            (right - left) / scale >= cropWidth * 0.18 &&
            bottom - top >= 12 * scale);
    }

    private static Int32Rect CreateAmountBounds(BitmapSource source, Int32Rect? anchorBounds)
    {
        var left = anchorBounds is Int32Rect horizontalAnchor
            ? Math.Clamp(
                horizontalAnchor.X + (int)Math.Round(horizontalAnchor.Height * 1.05),
                0,
                (int)Math.Floor(source.PixelWidth * 0.20))
            : (int)Math.Floor(source.PixelWidth * 0.02);
        // A user-selected payout region can be fairly tight. Preserve its full right edge so
        // the last large translucent digit is not clipped before whole-line OCR runs.
        var right = source.PixelWidth;
        var fallbackTop = (int)Math.Floor(source.PixelHeight * 0.24);
        var top = anchorBounds is Int32Rect anchor
            ? Math.Max(fallbackTop, anchor.Y + anchor.Height + Math.Max(2, source.PixelHeight / 120))
            : fallbackTop;
        var bottom = anchorBounds is Int32Rect measuredAnchor
            ? Math.Min(
                source.PixelHeight,
                top + (int)Math.Ceiling(Math.Max(
                    source.PixelHeight * 0.25,
                    measuredAnchor.Height * 4.2)))
            : Math.Min(source.PixelHeight, (int)Math.Ceiling(source.PixelHeight * 0.90));
        return new Int32Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private static Int32Rect RelativeBounds(BitmapSource source, double left, double top, double right, double bottom)
    {
        var x = (int)Math.Floor(source.PixelWidth * left);
        var y = (int)Math.Floor(source.PixelHeight * top);
        var finalX = (int)Math.Ceiling(source.PixelWidth * right);
        var finalY = (int)Math.Ceiling(source.PixelHeight * bottom);
        return new Int32Rect(x, y, Math.Max(1, finalX - x), Math.Max(1, finalY - y));
    }

    private static CroppedBitmap Crop(BitmapSource source, Int32Rect bounds)
    {
        var cropped = new CroppedBitmap(source, bounds);
        cropped.Freeze();
        return cropped;
    }

    private static string FormatCompetitors(IReadOnlyList<ScoredGroup> groups)
    {
        if (groups.Count <= 1)
        {
            return "None";
        }

        return string.Join(
            ", ",
            groups.Skip(1).Select(group =>
                $"{group.Amount.ToString("$#,##0", CultureInfo.GetCultureInfo("en-US"))} ({group.Score:0.0})"));
    }

    private static string FormatSymbols(IReadOnlyList<OcrSymbol>? symbols)
    {
        return symbols is not { Count: > 0 }
            ? string.Empty
            : string.Join(" ", symbols.Select(symbol =>
                $"{symbol.Text}:{symbol.Right - symbol.Left}x{symbol.Bottom - symbol.Top}"));
    }

    private static string TryCorrectRepeatedDigit(
        string rawText,
        IReadOnlyList<OcrSymbol> symbols,
        BitmapSource processedImage,
        out string evidence)
    {
        var digitSymbols = symbols.Where(symbol => symbol.Text.Length == 1 && char.IsDigit(symbol.Text[0])).ToList();
        var digits = rawText.Where(char.IsDigit).ToArray();
        if (digitSymbols.Count != digits.Length)
        {
            evidence = "Symbol count did not match the recognized digit count.";
            return rawText;
        }

        var grayscale = processedImage.Format == System.Windows.Media.PixelFormats.Gray8
            ? processedImage
            : new FormatConvertedBitmap(processedImage, System.Windows.Media.PixelFormats.Gray8, null, 0);
        if (grayscale.CanFreeze)
        {
            grayscale.Freeze();
        }

        var pixels = new byte[grayscale.PixelWidth * grayscale.PixelHeight];
        grayscale.CopyPixels(pixels, grayscale.PixelWidth, 0);
        var comparisons = new List<string>();
        for (var index = 0; index < digits.Length; index++)
        {
            if (digits[index] != '4')
            {
                continue;
            }

            var bestDifference = double.MaxValue;
            foreach (var oneIndex in Enumerable.Range(0, digits.Length).Where(candidateIndex => digits[candidateIndex] == '1'))
            {
                var difference = CalculateCellDifference(
                    pixels,
                    grayscale.PixelWidth,
                    digitSymbols[index],
                    digitSymbols[oneIndex]);
                bestDifference = Math.Min(bestDifference, difference);
            }

            if (bestDifference == double.MaxValue)
            {
                continue;
            }

            comparisons.Add($"digit {index + 1} 4-to-1 difference {bestDifference:0.0}%");
            if (bestDifference <= RepeatedDigitSimilarityThreshold)
            {
                digits[index] = '1';
            }
        }

        evidence = comparisons.Count == 0
            ? "No conservative repeated-digit comparison was available."
            : string.Join("; ", comparisons);
        return new string(digits);
    }

    private static double CalculateCellDifference(
        byte[] pixels,
        int stride,
        OcrSymbol left,
        OcrSymbol right)
    {
        const int sampleWidth = 32;
        const int sampleHeight = 48;
        long difference = 0;
        for (var y = 0; y < sampleHeight; y++)
        {
            var leftY = left.Top + Math.Min(left.Bottom - left.Top - 1, y * (left.Bottom - left.Top) / sampleHeight);
            var rightY = right.Top + Math.Min(right.Bottom - right.Top - 1, y * (right.Bottom - right.Top) / sampleHeight);
            for (var x = 0; x < sampleWidth; x++)
            {
                var leftX = left.Left + Math.Min(left.Right - left.Left - 1, x * (left.Right - left.Left) / sampleWidth);
                var rightX = right.Left + Math.Min(right.Right - right.Left - 1, x * (right.Right - right.Left) / sampleWidth);
                difference += Math.Abs(pixels[(leftY * stride) + leftX] - pixels[(rightY * stride) + rightX]);
            }
        }

        return difference * 100d / (sampleWidth * sampleHeight * 255d);
    }

    private static string GetEvidenceFamily(string method)
    {
        if (method.Contains("original", StringComparison.OrdinalIgnoreCase))
        {
            return "Original";
        }

        if (method.Contains("contrast", StringComparison.OrdinalIgnoreCase))
        {
            return method.Contains("adaptive", StringComparison.OrdinalIgnoreCase)
                ? "Adaptive"
                : "Contrast";
        }

        if (method.Contains("saturation", StringComparison.OrdinalIgnoreCase) ||
            method.Contains("green suppression", StringComparison.OrdinalIgnoreCase) ||
            method.Contains("closed text", StringComparison.OrdinalIgnoreCase) ||
            method.Contains("opened text", StringComparison.OrdinalIgnoreCase) ||
            method.Contains("neutral brightness", StringComparison.OrdinalIgnoreCase))
        {
            return "Threshold mask";
        }

        return method;
    }

    private sealed record AnchorResult(
        string RawText,
        string NormalizedText,
        float Confidence,
        Int32Rect? Bounds,
        bool IsExactMatch);

    private sealed record AmountResult(
        long Amount,
        string RawText,
        string NormalizedText,
        float Confidence,
        string Method,
        string SegmentationMode,
        bool HadDollar,
        bool ValidGrouping,
        bool IsFallback,
        PreprocessedImage Variant,
        IReadOnlyList<OcrSymbol>? Symbols);

    private sealed record ScoredGroup(
        long Amount,
        AmountResult Best,
        int AgreeingMethods,
        double Score);

    private sealed record CandidateDecision(
        ScoredGroup? Winner,
        IReadOnlyList<ScoredGroup> Groups,
        bool IsHighConfidence,
        bool HasEquallyPlausibleConflict);
}
