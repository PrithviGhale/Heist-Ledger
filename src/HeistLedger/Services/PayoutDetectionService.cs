using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GTAEarningsTracker.Models;

namespace GTAEarningsTracker.Services;

public sealed class PayoutDetectionService : IDisposable
{
    public const long MaximumPayout = 2_500_000_000;

    private static readonly Regex PositivePayoutRegex = new(
        @"(?<![-\d])\+\s*\$\s*(?<amount>(?:\d{1,3}(?:,\d{3})+|\d{1,3}(?: \d{3})+|\d+))(?![\d,.]|\s+\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AnchoredAmountRegex = new(
        @"(?<![-\d])(?:\+\s*)?(?:\$\s*)?(?<amount>(?:\d{1,3}(?:,\d{3})+|\d{1,3}(?: \d{3})+|\d+))(?![\d,.]|\s+\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NegativePrefixRegex = new(
        @"-\s*\$?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex InvalidNumericPrefixRegex = new(
        @"\d[\d, ]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex WordRegex = new(
        @"[A-Z]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex UnnecessaryPunctuationRegex = new(
        @"[^A-Z0-9$,+\-\s]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex HorizontalWhitespaceRegex = new(
        @"[\t ]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex WhitespaceRegex = new(
        @"\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ImagePreprocessingService _preprocessingService;
    private readonly OcrService _ocrService;
    private readonly OcrService _headingOcrService;
    private readonly OcrService _amountOcrService;
    private readonly OptimizedGtaResultScreenDetectionService _gtaResultScreenDetectionService;

    public PayoutDetectionService(
        ImagePreprocessingService preprocessingService,
        OcrService ocrService)
        : this(preprocessingService, ocrService, ocrService, ocrService)
    {
    }

    public PayoutDetectionService(
        ImagePreprocessingService preprocessingService,
        OcrService ocrService,
        OcrService headingOcrService,
        OcrService amountOcrService)
    {
        _preprocessingService = preprocessingService;
        _ocrService = ocrService;
        _headingOcrService = headingOcrService;
        _amountOcrService = amountOcrService;
        _gtaResultScreenDetectionService = new OptimizedGtaResultScreenDetectionService(
            headingOcrService,
            amountOcrService);
    }

    public PayoutAnalysisResult Analyze(BitmapSource capturedImage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capturedImage);
        cancellationToken.ThrowIfCancellationRequested();

        var fingerprint = ComputeFingerprint(capturedImage);
        var gtaResult = _gtaResultScreenDetectionService.Detect(
            capturedImage,
            fingerprint,
            ScannerDetectionMode.Fast,
            cancellationToken);
        if (gtaResult.Candidate is not null || gtaResult.Diagnostics.AnchorFound)
        {
            return CreateSpecializedAnalysis(gtaResult, fingerprint);
        }
        var variants = _preprocessingService.CreateVariants(capturedImage);
        OcrRecognitionResult? bestRecognition = null;
        DetectedPayout? bestCandidate = gtaResult.Candidate;

        foreach (var variant in variants)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var singleLineRecognition = _ocrService.Recognize(variant, cancellationToken);
            ConsiderRecognition(singleLineRecognition, fingerprint);

            cancellationToken.ThrowIfCancellationRequested();
            var multilineRecognition = _ocrService.RecognizeMultiline(variant, cancellationToken);
            ConsiderRecognition(multilineRecognition, fingerprint);
        }

        if (bestCandidate is not null)
        {
            return new PayoutAnalysisResult(
                bestCandidate,
                bestCandidate.OriginalOcrText,
                bestCandidate.Confidence,
                bestCandidate.PreprocessingMethod,
                fingerprint,
                gtaResult.Diagnostics);
        }

        return new PayoutAnalysisResult(
            null,
            bestRecognition?.Text ?? string.Empty,
            bestRecognition?.Confidence,
            bestRecognition?.PreprocessingMethod ?? "None",
            fingerprint,
            gtaResult.Diagnostics);

        void ConsiderRecognition(
            OcrRecognitionResult recognition,
            string imageHash)
        {
            if (bestRecognition is null || recognition.Confidence > bestRecognition.Confidence)
            {
                bestRecognition = recognition;
            }

            var plusCandidate = TryParsePlusSignCandidate(recognition, imageHash);
            ConsiderCandidate(plusCandidate);
        }

        void ConsiderCandidate(DetectedPayout? candidate)
        {
            if (candidate is null)
            {
                return;
            }

            // The dedicated two-region result-screen detector owns anchored TOTAL CASH EARNED
            // decisions. Generic OCR remains active only for legacy plus-sign payouts.
            if (bestCandidate?.DetectionMethod == PayoutDetectionMethod.GtaTotalCashEarnedResultScreen)
            {
                return;
            }

            if (bestCandidate is null ||
                candidate.Confidence > bestCandidate.Confidence ||
                (Math.Abs(candidate.Confidence - bestCandidate.Confidence) < 0.001 &&
                 candidate.DetectionMethod == PayoutDetectionMethod.PlusSignPayout))
            {
                bestCandidate = candidate;
            }
        }
    }

    public void Dispose()
    {
        foreach (var service in new[] { _ocrService, _headingOcrService, _amountOcrService }.Distinct())
        {
            service.Dispose();
        }
    }

    public PayoutAnalysisResult AnalyzeFastTotalCash(
        BitmapSource capturedImage,
        ScannerDetectionMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capturedImage);
        cancellationToken.ThrowIfCancellationRequested();
        var fingerprint = ComputeFingerprint(capturedImage);
        var result = _gtaResultScreenDetectionService.Detect(
            capturedImage,
            fingerprint,
            mode,
            cancellationToken);
        return CreateSpecializedAnalysis(result, fingerprint);
    }

    public PayoutAnalysisResult AnalyzeSelectedRegionProbe(
        BitmapSource capturedImage,
        ScannerDetectionMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capturedImage);
        cancellationToken.ThrowIfCancellationRequested();
        var fingerprint = ComputeFingerprint(capturedImage);
        var result = _gtaResultScreenDetectionService.Detect(
            capturedImage,
            fingerprint,
            mode,
            cancellationToken,
            selectedRegionProbe: true);
        return CreateSpecializedAnalysis(result, fingerprint);
    }

    private static PayoutAnalysisResult CreateSpecializedAnalysis(
        GtaResultScreenDetectionResult result,
        string fingerprint)
    {
        var candidate = result.Candidate;
        return new PayoutAnalysisResult(
            candidate,
            candidate?.OriginalOcrText ?? result.Diagnostics.AnchorOcrText,
            candidate?.Confidence ?? result.Diagnostics.AnchorConfidence,
            candidate?.PreprocessingMethod ?? "Fast GTA heading",
            fingerprint,
            result.Diagnostics);
    }

    public static DetectedPayout? ParseRecognizedText(
        string text,
        string preprocessingMethod,
        float confidence,
        string imageFingerprint = "test")
    {
        var recognition = new OcrRecognitionResult(text, confidence, preprocessingMethod);
        return TryParsePlusSignCandidate(recognition, imageFingerprint);
    }

    public static DetectedPayout? ParseTotalCashEarnedText(
        string text,
        string preprocessingMethod,
        float confidence,
        string imageFingerprint = "test")
    {
        var lines = SplitIntoOrderedLines(text);
        var recognition = new OcrRecognitionResult(text, confidence, preprocessingMethod, lines);
        return TryParseTotalCashEarnedCandidate(recognition, imageFingerprint);
    }

    private static DetectedPayout? TryParsePlusSignCandidate(
        OcrRecognitionResult recognition,
        string imageFingerprint)
    {
        if (string.IsNullOrWhiteSpace(recognition.Text))
        {
            return null;
        }

        foreach (Match match in PositivePayoutRegex.Matches(recognition.Text))
        {
            if (HasNegativePrefix(recognition.Text, match.Index))
            {
                continue;
            }

            if (!TryParseAmount(match.Groups["amount"].Value, out var amount))
            {
                continue;
            }

            var originalText = recognition.Text.Trim();
            return new DetectedPayout(
                amount,
                originalText,
                NormalizeForDuplicateKey(originalText),
                PayoutDetectionMethod.PlusSignPayout,
                recognition.PreprocessingMethod,
                recognition.Confidence,
                imageFingerprint);
        }

        return null;
    }

    private static DetectedPayout? TryParseTotalCashEarnedCandidate(
        OcrRecognitionResult recognition,
        string imageFingerprint)
    {
        if (string.IsNullOrWhiteSpace(recognition.Text))
        {
            return null;
        }

        var lines = recognition.TextLines is { Count: > 0 }
            ? recognition.TextLines
            : SplitIntoOrderedLines(recognition.Text);

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            if (!TryFindAnchor(lines, lineIndex, out var anchorLineIndex, out var anchorEndIndex))
            {
                continue;
            }

            var candidates = FindAmountsAfterAnchor(lines, anchorLineIndex, anchorEndIndex);
            var selectedAmount = SelectPreferredAmount(candidates, lines[anchorLineIndex]);
            if (selectedAmount is null)
            {
                continue;
            }

            var originalText = recognition.Text.Trim();
            return new DetectedPayout(
                selectedAmount.Amount,
                originalText,
                NormalizeForDuplicateKey(originalText),
                PayoutDetectionMethod.TotalCashEarned,
                recognition.PreprocessingMethod,
                recognition.Confidence,
                imageFingerprint,
                candidates.Select(candidate => candidate.Amount).Distinct().Skip(1).Any());
        }

        return null;
    }

    private static bool TryFindAnchor(
        IReadOnlyList<OcrTextLine> lines,
        int startingLineIndex,
        out int anchorLineIndex,
        out int anchorEndIndex)
    {
        if (TryFindAnchorInLine(lines[startingLineIndex].Text, out anchorEndIndex))
        {
            anchorLineIndex = startingLineIndex;
            return true;
        }

        if (startingLineIndex + 1 < lines.Count)
        {
            var combined = $"{lines[startingLineIndex].Text} {lines[startingLineIndex + 1].Text}";
            if (TryFindAnchorInLine(combined, out var combinedAnchorEnd))
            {
                anchorLineIndex = startingLineIndex + 1;
                var secondLineOffset = lines[startingLineIndex].Text.Length + 1;
                anchorEndIndex = Math.Clamp(
                    combinedAnchorEnd - secondLineOffset,
                    0,
                    lines[startingLineIndex + 1].Text.Length);
                return true;
            }
        }

        anchorLineIndex = -1;
        anchorEndIndex = -1;
        return false;
    }

    private static bool TryFindAnchorInLine(string rawLine, out int anchorEndIndex)
    {
        var uppercaseLine = rawLine.ToUpperInvariant();
        var words = WordRegex.Matches(uppercaseLine).Cast<Match>().ToList();

        for (var index = 0; index < words.Count; index++)
        {
            var word = words[index].Value;

            if (IsCloseWord(word, "TOTALCASHEARNED"))
            {
                anchorEndIndex = words[index].Index + words[index].Length;
                return true;
            }

            if (IsCloseWord(word, "TOTALCASH") &&
                index + 1 < words.Count &&
                IsCloseWord(words[index + 1].Value, "EARNED"))
            {
                anchorEndIndex = words[index + 1].Index + words[index + 1].Length;
                return true;
            }

            if (IsCloseWord(word, "TOTAL") &&
                index + 2 < words.Count &&
                IsCloseWord(words[index + 1].Value, "CASH") &&
                IsCloseWord(words[index + 2].Value, "EARNED"))
            {
                anchorEndIndex = words[index + 2].Index + words[index + 2].Length;
                return true;
            }
        }

        anchorEndIndex = -1;
        return false;
    }

    private static IReadOnlyList<AmountLineCandidate> FindAmountsAfterAnchor(
        IReadOnlyList<OcrTextLine> lines,
        int anchorLineIndex,
        int anchorEndIndex)
    {
        var candidates = new List<AmountLineCandidate>();
        var finalLineIndex = Math.Min(lines.Count - 1, anchorLineIndex + 2);

        for (var lineIndex = anchorLineIndex; lineIndex <= finalLineIndex; lineIndex++)
        {
            var line = lines[lineIndex];
            var searchableText = lineIndex == anchorLineIndex && anchorEndIndex < line.Text.Length
                ? line.Text[anchorEndIndex..]
                : lineIndex == anchorLineIndex
                    ? string.Empty
                    : line.Text;

            foreach (Match match in AnchoredAmountRegex.Matches(searchableText))
            {
                if (HasNegativePrefix(searchableText, match.Index) ||
                    InvalidNumericPrefixRegex.IsMatch(searchableText[..match.Index]) ||
                    !TryParseAmount(match.Groups["amount"].Value, out var amount))
                {
                    continue;
                }

                candidates.Add(new AmountLineCandidate(amount, lineIndex, line.Top, line.Bottom));
                break;
            }
        }

        return candidates;
    }

    private static AmountLineCandidate? SelectPreferredAmount(
        IReadOnlyList<AmountLineCandidate> candidates,
        OcrTextLine anchorLine)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        if (anchorLine.Bottom is int anchorBottom)
        {
            var belowAnchor = candidates
                .Where(candidate => candidate.Top is int top && top >= anchorBottom)
                .OrderBy(candidate => candidate.Top)
                .ThenBy(candidate => candidate.LineIndex)
                .FirstOrDefault();
            if (belowAnchor is not null)
            {
                return belowAnchor;
            }
        }

        return candidates.OrderBy(candidate => candidate.LineIndex).First();
    }

    private static IReadOnlyList<OcrTextLine> SplitIntoOrderedLines(string text)
    {
        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => new OcrTextLine(line, 0, null, null))
            .ToList();
    }

    private static string NormalizeLine(string text)
    {
        var uppercase = text.ToUpperInvariant();
        var withoutPunctuation = UnnecessaryPunctuationRegex.Replace(uppercase, " ");
        return HorizontalWhitespaceRegex.Replace(withoutPunctuation, " ").Trim();
    }

    private static string NormalizeForDuplicateKey(string text)
    {
        var orderedLines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(NormalizeLine)
            .Where(line => line.Length > 0);
        return string.Join(" | ", orderedLines);
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
                var substitutionCost = left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1;
                current[rightIndex] = Math.Min(
                    Math.Min(current[rightIndex - 1] + 1, previous[rightIndex] + 1),
                    previous[rightIndex - 1] + substitutionCost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static bool HasNegativePrefix(string text, int matchIndex)
    {
        return NegativePrefixRegex.IsMatch(text[..matchIndex]);
    }

    private static bool TryParseAmount(string rawAmount, out long amount)
    {
        var normalizedAmount = rawAmount
            .Replace(",", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);

        return long.TryParse(
                   normalizedAmount,
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out amount) &&
               amount > 0 &&
               amount <= MaximumPayout;
    }

    private static string ComputeFingerprint(BitmapSource source)
    {
        var converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = checked(converted.PixelWidth * 4);
        var pixels = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);
        return Convert.ToHexString(SHA256.HashData(pixels));
    }

    private sealed record AmountLineCandidate(long Amount, int LineIndex, int? Top, int? Bottom);
}
