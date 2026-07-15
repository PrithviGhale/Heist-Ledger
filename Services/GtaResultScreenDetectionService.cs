using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GTAEarningsTracker.Models;

namespace GTAEarningsTracker.Services;

public sealed record GtaResultScreenDetectionResult(
    DetectedPayout? Candidate,
    GtaResultScreenDiagnostics Diagnostics);

public sealed class GtaResultScreenDetectionService
{
    // Sparse-text confidence is frequently conservative for GTA's condensed heading.
    // The exact ordered three-word requirement remains the primary safety gate.
    public const float MinimumAnchorConfidence = 0.10f;
    public const double HighConfidenceScoreThreshold = 58;
    public const double CompetingCandidateMargin = 10;
    public const int MinimumAgreeingVariants = 2;

    private static readonly Regex AnchorWordRegex = new(
        "[A-Z0-9]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AmountTokenRegex = new(
        @"\$?\s*[0-9OoIl|](?:[0-9OoIl| ,.]*[0-9OoIl|])?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NegativeAmountRegex = new(
        @"-\s*\$?\s*[0-9OoIl|]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly OcrService _ocrService;
    private readonly GtaAmountPreprocessingService _amountPreprocessingService = new();

    public GtaResultScreenDetectionService(OcrService ocrService)
    {
        _ocrService = ocrService;
    }

    public GtaResultScreenDetectionResult Detect(
        BitmapSource capture,
        string imageFingerprint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capture);
        cancellationToken.ThrowIfCancellationRequested();

        var headingBounds = CreateRelativeBounds(capture, 0, 0.05, 1, 0.45);
        var headingCrop = Crop(capture, headingBounds);
        var headingVariants = CreateHeadingVariants(headingCrop);
        AnchorObservation? bestAnchor = null;

        foreach (var variant in headingVariants)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recognition = _ocrService.RecognizeMultiline(variant, cancellationToken);
            var observation = FindAnchor(recognition, variant.Scale, headingBounds);
            if (observation is not null &&
                (bestAnchor is null || observation.Confidence > bestAnchor.Confidence))
            {
                bestAnchor = observation;
            }
        }

        var amountBounds = CreateAmountBounds(capture, bestAnchor?.Bounds);
        if (bestAnchor is null || bestAnchor.Confidence < MinimumAnchorConfidence)
        {
            return new GtaResultScreenDetectionResult(
                null,
                CreateDiagnostics(
                    bestAnchor,
                    amountBounds,
                    null,
                    0,
                    string.Empty,
                    string.Empty,
                    0,
                    "None",
                    false,
                    "The required TOTAL CASH EARNED anchor was not confidently detected.",
                    [],
                    headingCrop,
                    null,
                    null));
        }

        var amountCrop = Crop(capture, amountBounds);
        var amountVariants = _amountPreprocessingService.CreateVariants(amountCrop);
        var diagnostics = new List<GtaAmountOcrDiagnostic>();
        var candidates = new List<AmountCandidate>();

        var untouchedAmount = amountVariants.First(variant =>
            variant.Method == "Original amount crop 1.0x");
        var signedSafetyResults = new[]
        {
            _ocrService.Recognize(untouchedAmount, cancellationToken),
            _ocrService.RecognizeMultiline(untouchedAmount, cancellationToken)
        };
        foreach (var safetyResult in signedSafetyResults)
        {
            diagnostics.Add(new GtaAmountOcrDiagnostic(
                $"{untouchedAmount.Method} (signed safety pass)",
                safetyResult.TextLines is null ? "SingleLine" : "SparseText",
                safetyResult.Text,
                string.Empty,
                QuickCapturePolicy.NormalizeConfidence(safetyResult.Confidence),
                null));
        }

        if (signedSafetyResults.Any(result => NegativeAmountRegex.IsMatch(result.Text)))
        {
            return new GtaResultScreenDetectionResult(
                null,
                CreateDiagnostics(
                    bestAnchor,
                    amountBounds,
                    null,
                    0,
                    "Signed safety pass",
                    "SingleLine / SparseText",
                    0,
                    "None",
                    false,
                    "A negative amount marker was detected below the anchor.",
                    diagnostics,
                    headingCrop,
                    amountCrop,
                    null));
        }

        foreach (var variant in amountVariants)
        {
            foreach (var segmentationMode in Enum.GetValues<OcrAmountPageSegmentation>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var recognition = _ocrService.RecognizeAmount(
                    variant,
                    segmentationMode,
                    cancellationToken);
                var confidence = QuickCapturePolicy.NormalizeConfidence(recognition.Confidence);
                var valid = TryNormalizeAmountText(
                    recognition.Text,
                    out var normalized,
                    out var amount,
                    out var hadDollarSign,
                    out var validGrouping);
                diagnostics.Add(new GtaAmountOcrDiagnostic(
                    variant.Method,
                    segmentationMode.ToString(),
                    recognition.Text,
                    normalized,
                    confidence,
                    valid ? amount : null));

                if (!valid)
                {
                    continue;
                }

                var digitCount = amount.ToString(CultureInfo.InvariantCulture).Length;
                var plausibleLargeText = HasPlausibleLargeTextBounds(
                    recognition.TextLines,
                    variant.Scale,
                    amountBounds.Width) || digitCount >= 5;
                if (!plausibleLargeText)
                {
                    continue;
                }

                candidates.Add(new AmountCandidate(
                    amount,
                    recognition.Text,
                    normalized,
                    confidence,
                    variant,
                    segmentationMode,
                    hadDollarSign,
                    validGrouping));
            }
        }

        if (candidates.Count == 0)
        {
            return new GtaResultScreenDetectionResult(
                null,
                CreateDiagnostics(
                    bestAnchor,
                    amountBounds,
                    null,
                    0,
                    string.Empty,
                    string.Empty,
                    0,
                    "None",
                    false,
                    "The anchor was found, but no valid amount was recognized below it.",
                    diagnostics,
                    headingCrop,
                    amountCrop,
                    null));
        }

        var scoredGroups = candidates
            .GroupBy(candidate => candidate.Amount)
            .Select(group => ScoreGroup(group.ToList(), bestAnchor.Confidence))
            .OrderByDescending(group => group.Score)
            .ToList();
        var winner = scoredGroups[0];
        var runnerUp = scoredGroups.Count > 1 ? scoredGroups[1] : null;
        var competing = runnerUp is not null &&
            winner.Score - runnerUp.Score < CompetingCandidateMargin;
        var exceptionallyStrongSingle =
            winner.AgreeingVariantCount == 1 &&
            winner.BestCandidate.Confidence >= 0.90f &&
            bestAnchor.Confidence >= 0.70f;
        var automaticAddAccepted =
            !competing &&
            winner.Score >= HighConfidenceScoreThreshold &&
            (winner.AgreeingVariantCount >= MinimumAgreeingVariants || exceptionallyStrongSingle);
        var reason = automaticAddAccepted
            ? $"Accepted: {winner.AgreeingVariantCount} OCR variants agreed and no similarly scored amount competed."
            : competing
                ? "Confirmation required: multiple plausible amounts had similar scores."
                : winner.AgreeingVariantCount < MinimumAgreeingVariants && !exceptionallyStrongSingle
                    ? "Confirmation required: fewer than two independent OCR variants agreed."
                    : "Confirmation required: the combined anchor and amount score was below the high-confidence threshold.";

        var selected = winner.BestCandidate;
        var combinedConfidence = (float)Math.Clamp(winner.Score / 100d, 0, 1);
        var originalText = $"{bestAnchor.RawText}\n{selected.RawText}".Trim();
        var candidate = new DetectedPayout(
            selected.Amount,
            originalText,
            $"{bestAnchor.NormalizedText} | {selected.NormalizedText}",
            PayoutDetectionMethod.GtaTotalCashEarnedResultScreen,
            selected.Variant.Method,
            combinedConfidence,
            imageFingerprint,
            competing,
            automaticAddAccepted);
        var competingText = string.Join(
            ", ",
            scoredGroups.Skip(1).Select(group =>
                $"{group.Amount.ToString("$#,##0", CultureInfo.GetCultureInfo("en-US"))} ({group.Score:0.0})"));

        return new GtaResultScreenDetectionResult(
            candidate,
            CreateDiagnostics(
                bestAnchor,
                amountBounds,
                selected.Amount,
                selected.Confidence,
                selected.Variant.Method,
                selected.SegmentationMode.ToString(),
                winner.AgreeingVariantCount,
                competingText.Length == 0 ? "None" : competingText,
                automaticAddAccepted,
                reason,
                diagnostics,
                headingCrop,
                amountCrop,
                GtaAmountPreprocessingService.DecodePreview(selected.Variant)));
    }

    public static bool TryNormalizeAmountText(
        string rawText,
        out string normalizedText,
        out long amount,
        out bool hadDollarSign,
        out bool validCommaGrouping)
    {
        normalizedText = string.Empty;
        amount = 0;
        hadDollarSign = false;
        validCommaGrouping = false;
        if (string.IsNullOrWhiteSpace(rawText) || rawText.Contains('-', StringComparison.Ordinal))
        {
            return false;
        }

        if (rawText.Count(character => character == '$') > 1)
        {
            return false;
        }

        var compactInput = Regex.Replace(rawText, @"\s+", string.Empty);
        if (compactInput.Any(character =>
                !char.IsDigit(character) &&
                character is not ('$' or ',' or '.' or 'O' or 'o' or 'I' or 'i' or 'l' or '|')))
        {
            return false;
        }

        var matches = AmountTokenRegex.Matches(rawText);
        if (matches.Count != 1)
        {
            return false;
        }

        var token = Regex.Replace(matches[0].Value, @"\s+", string.Empty);
        var dollarCount = token.Count(character => character == '$');
        if (dollarCount > 1 || (dollarCount == 1 && token[0] != '$'))
        {
            return false;
        }

        hadDollarSign = dollarCount == 1;
        if (hadDollarSign)
        {
            token = token[1..];
        }

        var corrected = new char[token.Length];
        for (var index = 0; index < token.Length; index++)
        {
            var character = token[index];
            if (char.IsDigit(character) || character is ',' or '.')
            {
                corrected[index] = character;
                continue;
            }

            var replacement = character switch
            {
                'O' or 'o' => '0',
                'I' or 'i' or 'l' or '|' => '1',
                _ => '\0'
            };
            var adjacentToDigit =
                (index > 0 && char.IsDigit(token[index - 1])) ||
                (index + 1 < token.Length && char.IsDigit(token[index + 1]));
            if (replacement == '\0' || !adjacentToDigit)
            {
                return false;
            }

            corrected[index] = replacement;
        }

        var correctedToken = new string(corrected);
        var containsSeparators = correctedToken.IndexOfAny([',', '.']) >= 0;
        if (containsSeparators)
        {
            var groups = correctedToken.Split([',', '.']);
            if (groups.Length < 2 ||
                groups[0].Length is < 1 or > 3 ||
                groups.Skip(1).Any(group => group.Length != 3 || !group.All(char.IsDigit)))
            {
                return false;
            }

            validCommaGrouping = true;
        }

        var digits = correctedToken.Replace(",", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal);
        if (digits.Length == 0 || !digits.All(char.IsDigit) ||
            !long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out amount) ||
            amount <= 0 || amount > PayoutDetectionService.MaximumPayout)
        {
            amount = 0;
            return false;
        }

        normalizedText = amount.ToString("$#,##0", CultureInfo.GetCultureInfo("en-US"));
        return true;
    }

    private static AnchorObservation? FindAnchor(
        OcrRecognitionResult recognition,
        double scale,
        Int32Rect headingBounds)
    {
        var lines = recognition.TextLines is { Count: > 0 }
            ? recognition.TextLines
            : recognition.Text
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(text => new OcrTextLine(text, recognition.Confidence, null, null))
                .ToList();

        for (var index = 0; index < lines.Count; index++)
        {
            if (IsAnchorText(lines[index].Text))
            {
                return CreateAnchorObservation(lines[index], recognition, scale, headingBounds);
            }

            if (index + 1 < lines.Count &&
                IsAnchorText($"{lines[index].Text} {lines[index + 1].Text}"))
            {
                var combined = new OcrTextLine(
                    $"{lines[index].Text} {lines[index + 1].Text}",
                    Math.Max(lines[index].Confidence, lines[index + 1].Confidence),
                    MinNullable(lines[index].Top, lines[index + 1].Top),
                    MaxNullable(lines[index].Bottom, lines[index + 1].Bottom),
                    MinNullable(lines[index].Left, lines[index + 1].Left),
                    MaxNullable(lines[index].Right, lines[index + 1].Right));
                return CreateAnchorObservation(combined, recognition, scale, headingBounds);
            }
        }

        if (IsAnchorText(recognition.Text))
        {
            return new AnchorObservation(
                recognition.Text.Trim(),
                NormalizeAnchorText(recognition.Text),
                QuickCapturePolicy.NormalizeConfidence(recognition.Confidence),
                null);
        }

        return null;
    }

    private static AnchorObservation CreateAnchorObservation(
        OcrTextLine line,
        OcrRecognitionResult recognition,
        double scale,
        Int32Rect headingBounds)
    {
        Int32Rect? bounds = null;
        if (line.Left is int left && line.Top is int top &&
            line.Right is int right && line.Bottom is int bottom)
        {
            var mappedLeft = headingBounds.X + (int)Math.Floor(left / scale);
            var mappedTop = headingBounds.Y + (int)Math.Floor(top / scale);
            var mappedRight = headingBounds.X + (int)Math.Ceiling(right / scale);
            var mappedBottom = headingBounds.Y + (int)Math.Ceiling(bottom / scale);
            bounds = new Int32Rect(
                mappedLeft,
                mappedTop,
                Math.Max(1, mappedRight - mappedLeft),
                Math.Max(1, mappedBottom - mappedTop));
        }

        return new AnchorObservation(
            line.Text.Trim(),
            NormalizeAnchorText(line.Text),
            Math.Max(
                QuickCapturePolicy.NormalizeConfidence(line.Confidence),
                QuickCapturePolicy.NormalizeConfidence(recognition.Confidence)),
            bounds);
    }

    private static bool IsAnchorText(string text)
    {
        var words = AnchorWordRegex.Matches(text.ToUpperInvariant())
            .Select(match => match.Value)
            .ToList();
        for (var index = 0; index < words.Count; index++)
        {
            if (IsCloseWord(words[index], "TOTALCASHEARNED"))
            {
                return true;
            }

            if (IsCloseWord(words[index], "TOTALCASH") &&
                index + 1 < words.Count && IsCloseWord(words[index + 1], "EARNED"))
            {
                return true;
            }

            if (IsCloseWord(words[index], "TOTAL") && index + 2 < words.Count &&
                IsCloseWord(words[index + 1], "CASH") &&
                IsCloseWord(words[index + 2], "EARNED"))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeAnchorText(string text)
    {
        return string.Join(
            " ",
            AnchorWordRegex.Matches(text.ToUpperInvariant()).Select(match => match.Value));
    }

    private static bool IsCloseWord(string candidate, string expected)
    {
        if (candidate.Equals(expected, StringComparison.Ordinal))
        {
            return true;
        }

        if (Math.Abs(candidate.Length - expected.Length) > 1)
        {
            return false;
        }

        return CalculateEditDistance(candidate, expected) <= 1;
    }

    private static int CalculateEditDistance(string left, string right)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            current[0] = leftIndex;
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                var substitution = left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1;
                current[rightIndex] = Math.Min(
                    Math.Min(current[rightIndex - 1] + 1, previous[rightIndex] + 1),
                    previous[rightIndex - 1] + substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static ScoredAmountGroup ScoreGroup(
        IReadOnlyList<AmountCandidate> candidates,
        float anchorConfidence)
    {
        var best = candidates.OrderByDescending(candidate => candidate.Confidence).First();
        var agreeingVariants = candidates.Select(candidate => candidate.Variant.Method).Distinct().Count();
        var digitCount = best.Amount.ToString(CultureInfo.InvariantCulture).Length;
        var score = (anchorConfidence * 22) +
            (best.Confidence * 34) +
            Math.Min(27, Math.Max(0, agreeingVariants - 1) * 7) +
            (best.HadDollarSign ? 5 : 0) +
            (best.ValidGrouping ? 5 : 0) +
            (digitCount is >= 4 and <= 10 ? 4 : 0) +
            3;
        return new ScoredAmountGroup(best.Amount, best, agreeingVariants, score);
    }

    private static bool HasPlausibleLargeTextBounds(
        IReadOnlyList<OcrTextLine>? lines,
        double scale,
        int amountCropWidth)
    {
        if (lines is not { Count: > 0 })
        {
            return true;
        }

        return lines.Any(line =>
            line.Left is int left && line.Right is int right &&
            (right - left) / scale >= amountCropWidth * 0.18);
    }

    private static Int32Rect CreateAmountBounds(BitmapSource source, Int32Rect? anchorBounds)
    {
        var left = Math.Max(0, (int)Math.Floor(source.PixelWidth * 0.02));
        var right = Math.Min(source.PixelWidth, (int)Math.Ceiling(source.PixelWidth * 0.98));
        var fallbackTop = (int)Math.Floor(source.PixelHeight * 0.25);
        var top = anchorBounds is Int32Rect anchor
            ? Math.Max(fallbackTop, anchor.Y + anchor.Height + Math.Max(3, source.PixelHeight / 100))
            : fallbackTop;
        var bottom = Math.Min(source.PixelHeight, (int)Math.Ceiling(source.PixelHeight * 0.92));
        if (bottom <= top)
        {
            top = fallbackTop;
            bottom = Math.Min(source.PixelHeight, (int)Math.Ceiling(source.PixelHeight * 0.95));
        }

        return new Int32Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private static Int32Rect CreateRelativeBounds(
        BitmapSource source,
        double left,
        double top,
        double right,
        double bottom)
    {
        var x = (int)Math.Floor(source.PixelWidth * left);
        var y = (int)Math.Floor(source.PixelHeight * top);
        var finalX = (int)Math.Ceiling(source.PixelWidth * right);
        var finalY = (int)Math.Ceiling(source.PixelHeight * bottom);
        return new Int32Rect(x, y, Math.Max(1, finalX - x), Math.Max(1, finalY - y));
    }

    private IReadOnlyList<PreprocessedImage> CreateHeadingVariants(BitmapSource headingCrop)
    {
        return _amountPreprocessingService.CreateVariants(headingCrop)
            .Where(variant =>
                variant.Method is
                    "Original amount crop 1.0x" or
                    "Grayscale 1.5x" or
                    "Increased contrast 1.5x" or
                    "Low-saturation bright-text threshold 1.5x")
            .Select(variant => variant with
            {
                Method = variant.Method.Replace("amount crop", "heading crop", StringComparison.Ordinal)
            })
            .ToList();
    }

    private static CroppedBitmap Crop(BitmapSource source, Int32Rect bounds)
    {
        var crop = new CroppedBitmap(source, bounds);
        crop.Freeze();
        return crop;
    }

    private static GtaResultScreenDiagnostics CreateDiagnostics(
        AnchorObservation? anchor,
        Int32Rect amountBounds,
        long? selectedAmount,
        float amountConfidence,
        string selectedMethod,
        string segmentationMode,
        int agreeingVariants,
        string competingCandidates,
        bool automaticAddAccepted,
        string automaticAddReason,
        IReadOnlyList<GtaAmountOcrDiagnostic> results,
        BitmapSource headingCrop,
        BitmapSource? amountCrop,
        BitmapSource? bestProcessed)
    {
#if DEBUG
        var headingPreview = headingCrop;
        var amountPreview = amountCrop;
        var processedPreview = bestProcessed;
#else
        BitmapSource? headingPreview = null;
        BitmapSource? amountPreview = null;
        BitmapSource? processedPreview = null;
#endif
        return new GtaResultScreenDiagnostics(
            anchor is not null,
            anchor?.RawText ?? string.Empty,
            anchor?.Confidence ?? 0,
            anchor?.Bounds,
            amountBounds,
            selectedAmount,
            amountConfidence,
            selectedMethod,
            segmentationMode,
            agreeingVariants,
            competingCandidates,
            automaticAddAccepted,
            automaticAddReason,
            results,
            headingPreview,
            amountPreview,
            processedPreview);
    }

    private static int? MinNullable(int? left, int? right)
    {
        return left.HasValue && right.HasValue ? Math.Min(left.Value, right.Value) : left ?? right;
    }

    private static int? MaxNullable(int? left, int? right)
    {
        return left.HasValue && right.HasValue ? Math.Max(left.Value, right.Value) : left ?? right;
    }

    private sealed record AnchorObservation(
        string RawText,
        string NormalizedText,
        float Confidence,
        Int32Rect? Bounds);

    private sealed record AmountCandidate(
        long Amount,
        string RawText,
        string NormalizedText,
        float Confidence,
        PreprocessedImage Variant,
        OcrAmountPageSegmentation SegmentationMode,
        bool HadDollarSign,
        bool ValidGrouping);

    private sealed record ScoredAmountGroup(
        long Amount,
        AmountCandidate BestCandidate,
        int AgreeingVariantCount,
        double Score);
}
