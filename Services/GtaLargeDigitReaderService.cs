using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GTAEarningsTracker.Models;

namespace GTAEarningsTracker.Services;

public sealed record BurstPayoutAnalysisResult(
    PayoutAnalysisResult Analysis,
    BitmapSource PreviewFrame,
    int CapturedFrameCount,
    int StableFrameCount,
    bool NeedsTargetedFallback = false);

public sealed record PayoutReaderVote(string ReaderId, long Amount, float Confidence);

public sealed record PayoutConsensusDecision(
    long? WinningAmount,
    int AgreeingReaders,
    bool HasEquallySupportedConflict,
    bool HasDigitCountConflict,
    bool MeetsExactAgreementPolicy);

public sealed class GtaLargeDigitReaderService
{
    public const int MinimumBurstFrames = 3;
    public const int MaximumBurstFrames = 5;
    public const int MinimumIndependentReadersForAutomaticAdd = 3;
    public const double MaximumStableFrameDifference = 14;

    private readonly PayoutDetectionService _detector;
    private readonly OcrService _amountOcr;
    private readonly FastGtaPreprocessingService _preprocessing = new();

    public GtaLargeDigitReaderService(
        PayoutDetectionService detector,
        OcrService amountOcr)
    {
        _detector = detector;
        _amountOcr = amountOcr;
    }

    public static PayoutConsensusDecision EvaluateConsensus(
        IReadOnlyList<PayoutReaderVote> votes,
        int? segmentedDigitCount)
    {
        var groups = votes
            .GroupBy(vote => vote.Amount)
            .Select(group => new
            {
                Amount = group.Key,
                Agreement = group.Select(vote => vote.ReaderId).Distinct(StringComparer.Ordinal).Count(),
                Confidence = group.Average(vote => vote.Confidence)
            })
            .OrderByDescending(group => group.Agreement)
            .ThenByDescending(group => group.Confidence)
            .ToArray();
        if (groups.Length == 0)
        {
            return new PayoutConsensusDecision(null, 0, false, false, false);
        }

        var winner = groups[0];
        var conflict = groups.Skip(1).Any(group => group.Agreement == winner.Agreement);
        var digitCountConflict = segmentedDigitCount is int count &&
            winner.Amount.ToString(CultureInfo.InvariantCulture).Length != count;
        return new PayoutConsensusDecision(
            winner.Amount,
            winner.Agreement,
            conflict,
            digitCountConflict,
            winner.Agreement >= MinimumIndependentReadersForAutomaticAdd &&
                !conflict &&
                !digitCountConflict);
    }

    public BurstPayoutAnalysisResult Analyze(
        IReadOnlyList<BitmapSource> frames,
        ScannerDetectionMode mode,
        CancellationToken cancellationToken,
        PayoutAnalysisResult? initialAnalysis = null,
        bool forceTargetedVerification = false)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0)
        {
            throw new ArgumentException("At least one captured frame is required.", nameof(frames));
        }

        var stopwatch = Stopwatch.StartNew();
        var firstAnalysis = initialAnalysis ??
            _detector.AnalyzeSelectedRegionProbe(frames[0], mode, cancellationToken);
        var firstDiagnostics = firstAnalysis.GtaResultDiagnostics;
        if (firstDiagnostics is null || !firstDiagnostics.AnchorFound ||
            !IsValidCrop(firstDiagnostics.AmountCropBounds, frames[0]))
        {
            return new BurstPayoutAnalysisResult(firstAnalysis, frames[0], frames.Count, 0);
        }

        var stableFrames = SelectStableFrames(frames, cancellationToken);
        if (stableFrames.Count == 0)
        {
            stableFrames.Add(frames[0]);
        }

        var amountCropBounds = forceTargetedVerification
            ? ExpandAmountCropForLeadingGlyphs(firstDiagnostics, frames[0])
            : firstDiagnostics.AmountCropBounds;
        var amountCrops = stableFrames
            .Where(frame => IsValidCrop(amountCropBounds, frame))
            .Select(frame => Crop(frame, amountCropBounds))
            .ToList();
        if (amountCrops.Count == 0)
        {
            return new BurstPayoutAnalysisResult(firstAnalysis, frames[0], frames.Count, stableFrames.Count);
        }

        var readers = new List<ReaderResult>();
        var ocrDiagnostics = new List<GtaAmountOcrDiagnostic>();
        var priorStableResults = initialAnalysis?.GtaResultDiagnostics?.AmountOcrResults
            .Where(result => result.PreprocessingMethod.StartsWith("Stable frame", StringComparison.Ordinal) &&
                result.ParsedAmount is not null)
            .Take(MinimumBurstFrames)
            .ToArray() ?? [];
        for (var index = 0; index < priorStableResults.Length && index < amountCrops.Count; index++)
        {
            var prior = priorStableResults[index];
            readers.Add(new ReaderResult(
                $"Stable frame {index + 1}",
                prior.ParsedAmount!.Value,
                prior.RawText,
                prior.Confidence,
                prior.PreprocessingMethod,
                prior.PageSegmentationMode,
                _preprocessing.CreateLowSaturationVariant(amountCrops[index]),
                null));
        }

        if (priorStableResults.Length == 0)
        {
            for (var index = 0; index < Math.Min(MinimumBurstFrames, amountCrops.Count); index++)
            {
                ReadWholeLine($"Stable frame {index + 1}", amountCrops[index], VariantKind.LowSaturation);
            }
        }

        var fastConsensus = EvaluateConsensus(
            readers.Select(reader => new PayoutReaderVote(reader.ReaderName, reader.Amount, reader.Confidence)).ToArray(),
            GetRepresentativeDigitCount(readers));
        var continuingTargetedFallback = forceTargetedVerification ||
            initialAnalysis is not null && frames.Count > MinimumBurstFrames;
        var fastPathComplete = fastConsensus.MeetsExactAgreementPolicy && !continuingTargetedFallback;
        var runTargetedFallback = !fastPathComplete && frames.Count > MinimumBurstFrames;
        if (runTargetedFallback)
        {
            for (var index = MinimumBurstFrames; index < Math.Min(MaximumBurstFrames, amountCrops.Count); index++)
            {
                ReadWholeLine($"Fallback frame {index + 1}", amountCrops[index], VariantKind.LowSaturation);
            }

            var brightComposite = CreateBrightLowSaturationComposite(amountCrops);
            var medianComposite = CreateMedianLuminanceComposite(amountCrops);
            ReadWholeLine(
                "Neutral brightness composite",
                medianComposite,
                VariantKind.NeutralBrightness,
                OcrAmountPageSegmentation.SingleLine);
            ReadWholeLine(
                "Closed-text composite",
                medianComposite,
                VariantKind.ClosedMask,
                OcrAmountPageSegmentation.SingleLine);
            ReadWholeLine("Bright low-saturation composite", brightComposite, VariantKind.GreenSuppression);
            CorrectDollarMergedLeadingDigit(readers, amountCrops[0], ocrDiagnostics, cancellationToken);
            RecoverTrailingDigitWhenSupported(readers, firstDiagnostics.AmountOcrResults);
            AddProbeVoteWhenGeometrySupports(readers, firstAnalysis.Candidate);
            RemoveGeometricallyIncompleteReadersWhenCompleteEvidenceExists(readers);
        }

        var grouped = readers
            .GroupBy(reader => reader.Amount)
            .Select(group => new
            {
                Amount = group.Key,
                Readers = group.ToList(),
                Agreement = group.Select(reader => reader.ReaderName).Distinct(StringComparer.Ordinal).Count(),
                AverageConfidence = group.Average(reader => reader.Confidence)
            })
            .OrderByDescending(group => group.Agreement)
            .ThenByDescending(group => group.AverageConfidence)
            .ToList();
        if (grouped.Count == 0)
        {
            var noReaderDiagnostics = firstDiagnostics with
            {
                AmountOcrResults = firstDiagnostics.AmountOcrResults.Concat(ocrDiagnostics).ToArray(),
                StableFrameCount = stableFrames.Count,
                IndependentReaderAgreement = 0,
                AutomaticAddAccepted = false,
                AutomaticAddReason = "Confirmation unavailable: no burst reader produced a complete valid digit sequence."
            };
            return new BurstPayoutAnalysisResult(
                firstAnalysis with { GtaResultDiagnostics = noReaderDiagnostics },
                frames[0],
                frames.Count,
                stableFrames.Count,
                firstAnalysis.Candidate is not null && frames.Count <= MinimumBurstFrames);
        }

        var winner = grouped[0];
        var competing = grouped.Skip(1)
            .Where(group => group.Agreement >= winner.Agreement - 1)
            .ToList();
        var representative = winner.Readers
            .OrderByDescending(reader => reader.Symbols is { Count: > 0 })
            .ThenByDescending(reader => reader.Confidence)
            .First();
        var characterOcrAttempts = 0;
        var segmentedDigits = string.Empty;
        var digitDiagnostics = runTargetedFallback
            ? ReadIndividualDigits(
                representative,
                cancellationToken,
                out segmentedDigits,
                out characterOcrAttempts)
            : [];
        var segmentedAmount = runTargetedFallback &&
            long.TryParse(segmentedDigits, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedSegmented)
                ? parsedSegmented
                : (long?)null;
        var uncertainPositions = digitDiagnostics
            .Where(digit => digit.IsUncertain)
            .Select(digit => digit.Position.ToString(CultureInfo.InvariantCulture))
            .ToArray();
        var expectedDigitCount = winner.Amount.ToString(CultureInfo.InvariantCulture).Length;
        var symbolDigitCount = representative.Symbols?.Count(symbol =>
            symbol.Text.Length == 1 && char.IsDigit(symbol.Text[0]));
        var estimatedDigitCount = GetRepresentativeDigitCount([representative]);
        var segmentationAgrees = runTargetedFallback
            ? segmentedAmount == winner.Amount &&
                digitDiagnostics.Count == expectedDigitCount &&
                estimatedDigitCount == expectedDigitCount
            : symbolDigitCount == expectedDigitCount && estimatedDigitCount == expectedDigitCount;
        var hasConflict = competing.Count > 0;
        var confidence = (float)Math.Clamp(
            (Math.Min(1, winner.Agreement / 4d) * 0.50) +
            (winner.AverageConfidence * 0.25) +
            (firstDiagnostics.AnchorConfidence * 0.25),
            0,
            1);
        var strictAutomaticAdd = winner.Agreement >= MinimumIndependentReadersForAutomaticAdd &&
            segmentationAgrees &&
            uncertainPositions.Length == 0 &&
            !hasConflict &&
            firstDiagnostics.AnchorConfidence >= 0.60f &&
            confidence >= QuickCapturePolicy.AutomaticAddConfidenceThreshold;
        var competitorText = grouped.Count <= 1
            ? "None"
            : string.Join(", ", grouped.Skip(1).Select(group =>
                $"{group.Amount.ToString("$#,##0", CultureInfo.GetCultureInfo("en-US"))} ({group.Agreement} readers)"));
        var reason = strictAutomaticAdd
            ? $"Accepted: {winner.Agreement} independent readers and segmented digits agreed exactly with no competing value."
            : hasConflict
                ? "Confirmation required: similarly supported whole-number candidates disagree."
                : !segmentationAgrees
                    ? "Confirmation required: whole-line and segmented digit counts or values disagree."
                    : uncertainPositions.Length > 0
                        ? $"Confirmation required: uncertain digit positions {string.Join(", ", uncertainPositions)}."
                        : $"Confirmation required: only {winner.Agreement} independent readers agreed; {MinimumIndependentReadersForAutomaticAdd} are required.";
        var rawText = $"{firstDiagnostics.AnchorOcrText}\n{representative.RawText}".Trim();
        var candidate = new DetectedPayout(
            winner.Amount,
            rawText,
            $"TOTAL CASH EARNED | {winner.Amount.ToString(CultureInfo.InvariantCulture)}",
            PayoutDetectionMethod.GtaTotalCashEarnedResultScreen,
            $"Burst consensus: {representative.Method}",
            confidence,
            firstAnalysis.ImageFingerprint,
            hasConflict || !segmentationAgrees,
            strictAutomaticAdd);

        stopwatch.Stop();
        var performance = firstDiagnostics.Performance is null
            ? null
            : firstDiagnostics.Performance with
            {
                TotalScanMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
                DetectionPath = runTargetedFallback ? "Selected-region targeted fallback" : "Selected-region fast burst",
                PreprocessingVariantsAttempted =
                    firstDiagnostics.Performance.PreprocessingVariantsAttempted + ocrDiagnostics.Count + characterOcrAttempts
            };
        var combinedDiagnostics = firstDiagnostics with
        {
            AmountCropBounds = amountCropBounds,
            SelectedAmount = winner.Amount,
            AmountConfidence = confidence,
            SelectedPreprocessingMethod = representative.Method,
            SelectedPageSegmentationMode = representative.PageSegmentationMode,
            AgreeingVariantCount = winner.Agreement,
            CompetingCandidates = competitorText,
            AutomaticAddAccepted = strictAutomaticAdd,
            AutomaticAddReason = reason,
            AmountOcrResults = firstDiagnostics.AmountOcrResults.Concat(ocrDiagnostics).ToArray(),
            AmountCropPreview = amountCrops[0],
            BestProcessedAmountPreview = Decode(representative.Variant),
            Performance = performance,
            DigitOcrResults = digitDiagnostics,
            StableFrameCount = stableFrames.Count,
            IndependentReaderAgreement = winner.Agreement,
            UncertainDigitPositions = uncertainPositions.Length == 0
                ? "None"
                : string.Join(", ", uncertainPositions)
        };
        var analysis = new PayoutAnalysisResult(
            candidate,
            rawText,
            confidence,
            candidate.PreprocessingMethod,
            firstAnalysis.ImageFingerprint,
            combinedDiagnostics);
        return new BurstPayoutAnalysisResult(
            analysis,
            frames[0],
            frames.Count,
            stableFrames.Count,
            !fastPathComplete && !runTargetedFallback);

        void ReadWholeLine(
            string readerName,
            BitmapSource crop,
            VariantKind kind,
            OcrAmountPageSegmentation segmentationMode = OcrAmountPageSegmentation.RawLine)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var variant = SelectVariant(crop, kind);
            var recognition = _amountOcr.RecognizeAmount(
                variant,
                segmentationMode,
                cancellationToken);
            var valid = GtaResultScreenDetectionService.TryNormalizeAmountText(
                recognition.Text,
                out var normalized,
                out var amount,
                out _,
                out _);
            var confidenceValue = QuickCapturePolicy.NormalizeConfidence(recognition.Confidence);
            ocrDiagnostics.Add(new GtaAmountOcrDiagnostic(
                $"{readerName}: {variant.Method}",
                segmentationMode.ToString(),
                recognition.Text,
                normalized,
                confidenceValue,
                valid ? amount : null,
                FormatSymbols(recognition.Symbols)));
            if (valid && amount.ToString(CultureInfo.InvariantCulture).Length >= 4)
            {
                readers.Add(new ReaderResult(
                    readerName,
                    amount,
                    recognition.Text,
                    confidenceValue,
                    variant.Method,
                    segmentationMode.ToString(),
                    variant,
                    recognition.Symbols));
            }
        }
    }

    private static int? GetRepresentativeDigitCount(IReadOnlyList<ReaderResult> readers)
    {
        var winner = readers
            .GroupBy(reader => reader.Amount)
            .OrderByDescending(group => group.Select(reader => reader.ReaderName).Distinct().Count())
            .FirstOrDefault();
        var representative = winner?.FirstOrDefault();
        var symbols = representative?.Symbols?
            .Where(symbol => symbol.Text.Length == 1 && char.IsDigit(symbol.Text[0]))
            .OrderBy(symbol => symbol.Left)
            .ToArray();
        if (symbols is not { Length: > 0 })
        {
            return null;
        }

        var widths = symbols
            .Select(symbol => symbol.Right - symbol.Left)
            .OrderBy(width => width)
            .ToArray();
        // Use the lower median. GTA's dollar sign is commonly merged into the first
        // digit box and one other glyph can be abnormally wide; neither should make
        // the representative digit width drift upward for short amounts.
        var typicalWidth = widths[(widths.Length - 1) / 2];
        var firstWidthRatio = (symbols[0].Right - symbols[0].Left) / (double)typicalWidth;
        // A modestly wide first box is normally "$1". A much wider box can contain
        // the dollar sign plus multiple digits, as in the GTA $12,418,670 overlay.
        var leadingMergedDigits = firstWidthRatio > 2.45
            ? Math.Max(0, (int)Math.Ceiling(firstWidthRatio) - 2)
            : 0;
        var interiorMergedDigits = symbols
            .Skip(1)
            .Sum(symbol =>
            {
                var ratio = (symbol.Right - symbol.Left) / (double)typicalWidth;
                return ratio > 1.25
                    ? Math.Max(1, (int)Math.Round(ratio) - 1)
                    : 0;
            });
        return symbols.Length + leadingMergedDigits + interiorMergedDigits;
    }

    private static void RemoveGeometricallyIncompleteReadersWhenCompleteEvidenceExists(
        List<ReaderResult> readers)
    {
        var completeAmounts = readers
            .Where(reader =>
            {
                if (reader.ReaderName.StartsWith("Leading digit verification", StringComparison.Ordinal))
                {
                    return true;
                }

                var estimatedCount = GetRepresentativeDigitCount([reader]);
                return reader.Symbols is { Count: > 0 } &&
                    estimatedCount == reader.Amount.ToString(CultureInfo.InvariantCulture).Length;
            })
            .Select(reader => reader.Amount)
            .ToHashSet();
        if (completeAmounts.Count == 0)
        {
            return;
        }

        readers.RemoveAll(reader => !completeAmounts.Contains(reader.Amount));
    }

    private static void AddProbeVoteWhenGeometrySupports(
        List<ReaderResult> readers,
        DetectedPayout? probeCandidate)
    {
        if (probeCandidate is null ||
            readers.Any(reader => string.Equals(reader.ReaderName, "Selected-region probe", StringComparison.Ordinal)))
        {
            return;
        }

        var support = readers
            .Where(reader => reader.Amount == probeCandidate.Amount && reader.Symbols is { Count: > 0 })
            .FirstOrDefault(reader =>
                GetRepresentativeDigitCount([reader]) ==
                reader.Amount.ToString(CultureInfo.InvariantCulture).Length);
        if (support is null)
        {
            return;
        }

        readers.Add(new ReaderResult(
            "Selected-region probe",
            support.Amount,
            probeCandidate.OriginalOcrText,
            probeCandidate.Confidence,
            probeCandidate.PreprocessingMethod,
            "Probe agreement",
            support.Variant,
            null));
    }

    private void CorrectDollarMergedLeadingDigit(
        List<ReaderResult> readers,
        BitmapSource originalAmountCrop,
        List<GtaAmountOcrDiagnostic> ocrDiagnostics,
        CancellationToken cancellationToken)
    {
        var baseGroup = readers
            .GroupBy(reader => reader.Amount)
            .OrderByDescending(group => group.Select(reader => reader.ReaderName).Distinct().Count())
            .FirstOrDefault();
        var source = baseGroup?
            .Where(reader => reader.Symbols is { Count: > 1 })
            .OrderByDescending(reader => reader.Confidence)
            .FirstOrDefault();
        if (baseGroup is null || source?.Symbols is not { Count: > 1 })
        {
            return;
        }

        var symbols = source.Symbols
            .Where(symbol => symbol.Text.Length == 1 && char.IsDigit(symbol.Text[0]))
            .OrderBy(symbol => symbol.Left)
            .ToArray();
        if (symbols.Length < 2)
        {
            return;
        }

        var widths = symbols.Skip(1)
            .Select(symbol => Math.Max(1, symbol.Right - symbol.Left))
            .OrderBy(width => width)
            .ToArray();
        var typicalWidth = widths[(widths.Length - 1) / 2];
        var first = symbols[0];
        if ((first.Right - first.Left) / (double)typicalWidth < 1.55)
        {
            return;
        }

        // The first Tesseract symbol commonly contains "$9". Re-read that whole
        // merged glyph in SingleChar mode; the digit whitelist makes Tesseract
        // ignore the dollar sign while retaining all of the condensed digit.
        var sourceImage = Decode(source.Variant);
        var paddingY = Math.Max(2, (first.Bottom - first.Top) / 14);
        var bounds = ClampBounds(sourceImage, new Int32Rect(
            first.Left,
            first.Top - paddingY,
            first.Right - first.Left,
            (first.Bottom - first.Top) + (paddingY * 2)));
        var cell = Crop(sourceImage, bounds);
        var scale = source.Variant.Scale <= 0 ? 1 : source.Variant.Scale;
        var originalBounds = ClampBounds(originalAmountCrop, new Int32Rect(
            (int)Math.Floor(first.Left / scale),
            (int)Math.Floor((first.Top - paddingY) / scale),
            Math.Max(1, (int)Math.Ceiling((first.Right - first.Left) / scale)),
            Math.Max(1, (int)Math.Ceiling(((first.Bottom - first.Top) + (paddingY * 2)) / scale))));
        var originalCell = Crop(originalAmountCrop, originalBounds);
        var variants = new[]
        {
            _preprocessing.CreateCharacterVariant(cell, new Int32Rect(0, 0, cell.PixelWidth, cell.PixelHeight),
                "Dollar-merged leading digit"),
            _preprocessing.CreateClosedTextVariant(cell),
            _preprocessing.CreateLocalContrastVariant(originalCell),
            _preprocessing.CreateNeutralBrightnessVariant(originalCell),
            _preprocessing.CreateClosedTextVariant(originalCell)
        };
        var votes = new List<(string Prefix, float Confidence, PreprocessedImage Variant)>();
        foreach (var variant in variants)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recognition = _amountOcr.RecognizeAmountCharacter(variant, cancellationToken);
            var recognizedDigits = new string(recognition.Text.Where(char.IsDigit).ToArray());
            var confidence = QuickCapturePolicy.NormalizeConfidence(recognition.Confidence);
            var topology = AnalyzeEnclosedRegions(Decode(variant));
            // In the GTA font, a dollar-merged 9 retains one distinctly upper
            // enclosed loop even when OCR returns blank or calls it a 3/5. A 3 or
            // 5 has no loop; 6 has a lower loop and 0 is centered.
            if (topology.Count == 1 && topology.TopmostCenterYRatio is < 0.45 && recognizedDigits != "9")
            {
                recognizedDigits = "9";
                confidence = Math.Max(confidence, 0.65f);
            }
            ocrDiagnostics.Add(new GtaAmountOcrDiagnostic(
                variant.Method,
                "SingleChar",
                recognition.Text,
                recognizedDigits,
                confidence,
                long.TryParse(recognizedDigits, NumberStyles.None, CultureInfo.InvariantCulture, out var recognizedPrefix)
                    ? recognizedPrefix
                    : null,
                $"{FormatSymbols(recognition.Symbols)}; loops={topology.Count}; upper={topology.TopmostCenterYRatio:0.00}"));
            if (recognizedDigits.Length is > 0 and <= 3)
            {
                votes.Add((recognizedDigits, confidence, variant));
            }
        }

        var oldText = baseGroup.Key.ToString(CultureInfo.InvariantCulture);
        var voteGroup = votes
            .Where(vote => vote.Prefix != oldText[..1])
            .GroupBy(vote => vote.Prefix, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenByDescending(group => group.Average(vote => vote.Confidence))
            .FirstOrDefault();
        if (voteGroup is null)
        {
            return;
        }

        var recoveredText = voteGroup.Key + oldText[1..];
        if (!long.TryParse(recoveredText, NumberStyles.None, CultureInfo.InvariantCulture, out var recoveredAmount) ||
            recoveredAmount <= 0 || recoveredAmount > 2_500_000_000)
        {
            return;
        }

        var matchingWholeLineEvidence = readers.Any(reader => reader.Amount == recoveredAmount);
        var evidence = voteGroup.ToArray();
        if (evidence.Length < 2 && !matchingWholeLineEvidence)
        {
            return;
        }

        readers.RemoveAll(reader => reader.Amount == baseGroup.Key);
        for (var index = 0; index < evidence.Length; index++)
        {
            readers.Add(new ReaderResult(
                $"Leading digit verification {index + 1}",
                recoveredAmount,
                evidence[index].Prefix,
                evidence[index].Confidence,
                $"Isolated leading cell: {evidence[index].Variant.Method}",
                "SingleChar",
                source.Variant,
                index == 0 ? source.Symbols : null));
        }
    }

    private void RecoverLeadingMergedDigits(
        List<ReaderResult> readers,
        List<GtaAmountOcrDiagnostic> ocrDiagnostics,
        CancellationToken cancellationToken)
    {
        var source = readers
            .Where(reader => reader.Symbols is { Count: > 1 })
            .Select(reader => new
            {
                Reader = reader,
                Missing = GetRepresentativeDigitCount([reader]) -
                    reader.Amount.ToString(CultureInfo.InvariantCulture).Length,
                Agreement = readers.Count(other => other.Amount == reader.Amount)
            })
            .Where(item => item.Missing is > 0 and <= 3)
            .OrderByDescending(item => item.Agreement)
            .ThenByDescending(item => item.Reader.Confidence)
            .Select(item => item.Reader)
            .FirstOrDefault();
        if (source?.Symbols is not { Count: > 1 })
        {
            return;
        }

        var symbols = source.Symbols
            .Where(symbol => symbol.Text.Length == 1 && char.IsDigit(symbol.Text[0]))
            .OrderBy(symbol => symbol.Left)
            .ToArray();
        if (symbols.Length < 2)
        {
            return;
        }

        var widths = symbols
            .Select(symbol => Math.Max(1, symbol.Right - symbol.Left))
            .OrderBy(width => width)
            .ToArray();
        var typicalWidth = widths[(widths.Length - 1) / 2];
        var first = symbols[0];
        var firstRatio = (first.Right - first.Left) / (double)typicalWidth;
        if (firstRatio <= 1.75)
        {
            return;
        }

        // Tesseract often groups "$96" as one symbol and reports only the 6. The
        // right edge still aligns with the remaining digits, so split the box into
        // equal right-aligned digit cells and OCR only the missing leading cells.
        var digitSlots = Math.Max(2, (int)Math.Ceiling(firstRatio) - 1);
        var missingDigits = digitSlots - 1;
        var sourceImage = Decode(source.Variant);
        var mergedBounds = ClampBounds(sourceImage, new Int32Rect(
            first.Right - (digitSlots * typicalWidth),
            first.Top,
            digitSlots * typicalWidth,
            Math.Max(1, first.Bottom - first.Top)));
        var mergedVariant = _preprocessing.CreateCharacterVariant(
            sourceImage,
            mergedBounds,
            "Leading merged digit group");
        var mergedRecognition = _amountOcr.RecognizeAmount(
            mergedVariant,
            OcrAmountPageSegmentation.SingleLine,
            cancellationToken);
        var mergedDigits = new string(mergedRecognition.Text.Where(char.IsDigit).ToArray());
        var mergedConfidence = QuickCapturePolicy.NormalizeConfidence(mergedRecognition.Confidence);
        ocrDiagnostics.Add(new GtaAmountOcrDiagnostic(
            mergedVariant.Method,
            OcrAmountPageSegmentation.SingleLine.ToString(),
            mergedRecognition.Text,
            mergedDigits,
            mergedConfidence,
            long.TryParse(mergedDigits, NumberStyles.None, CultureInfo.InvariantCulture, out var mergedAmount)
                ? mergedAmount
                : null,
            FormatSymbols(mergedRecognition.Symbols)));
        var recognizedFirstDigit = source.Amount.ToString(CultureInfo.InvariantCulture)[0];
        if (mergedDigits.Length == digitSlots && mergedDigits[^1] == recognizedFirstDigit)
        {
            AddRecoveredReader(mergedDigits[..^1], mergedConfidence, "group OCR");
            return;
        }

        var prefix = new List<char>(missingDigits);
        var prefixConfidence = 1f;
        for (var index = 0; index < missingDigits; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slotLeft = first.Right - (digitSlots * typicalWidth) + (index * typicalWidth);
            var verticalPadding = Math.Max(2, (first.Bottom - first.Top) / 14);
            // Keep the left edge away from the adjoining dollar-sign stroke. It is
            // close enough to turn a clean GTA 9 into a false 5 in SingleChar mode.
            var bounds = ClampBounds(sourceImage, new Int32Rect(
                slotLeft,
                first.Top - verticalPadding,
                typicalWidth,
                (first.Bottom - first.Top) + (verticalPadding * 2)));
            var variant = _preprocessing.CreateCharacterVariant(
                sourceImage,
                bounds,
                $"Leading merged digit {index + 1}");
            var recognition = _amountOcr.RecognizeAmountCharacter(variant, cancellationToken);
            var digit = recognition.Text.FirstOrDefault(char.IsDigit);
            var confidence = QuickCapturePolicy.NormalizeConfidence(recognition.Confidence);
            var topology = AnalyzeEnclosedRegions(Crop(sourceImage, bounds));
            if (digit == default || digit == '5')
            {
                var closedVariant = _preprocessing.CreateClosedTextVariant(Crop(sourceImage, bounds));
                var closedRecognition = _amountOcr.RecognizeAmountCharacter(closedVariant, cancellationToken);
                var closedDigit = closedRecognition.Text.FirstOrDefault(char.IsDigit);
                var closedConfidence = QuickCapturePolicy.NormalizeConfidence(closedRecognition.Confidence);
                var closedTopology = AnalyzeEnclosedRegions(Decode(closedVariant));
                ocrDiagnostics.Add(new GtaAmountOcrDiagnostic(
                    $"Leading merged digit {index + 1}: closed loop",
                    "SingleChar",
                    closedRecognition.Text,
                    closedDigit == default ? string.Empty : closedDigit.ToString(),
                    closedConfidence,
                    closedDigit == default ? null : closedDigit - '0',
                    $"{FormatSymbols(closedRecognition.Symbols)}; loops={closedTopology.Count}; upper={closedTopology.TopmostCenterYRatio:0.00}"));
                if (closedDigit != default &&
                    (closedConfidence >= confidence || closedDigit == '9' && closedTopology.Count == 1))
                {
                    recognition = closedRecognition;
                    digit = closedDigit;
                    confidence = closedConfidence;
                    topology = closedTopology;
                }
            }
            // The GTA condensed 9 can lose its right stem during thresholding and
            // Tesseract then calls it a 5. A 5 has no enclosed region; accept the
            // correction only when the isolated cell has exactly one upper loop.
            if (digit == '5' && topology.Count == 1 && topology.TopmostCenterYRatio is < 0.55)
            {
                digit = '9';
                confidence = Math.Min(confidence, 0.72f);
            }
            ocrDiagnostics.Add(new GtaAmountOcrDiagnostic(
                variant.Method,
                "SingleChar",
                recognition.Text,
                digit == default ? string.Empty : digit.ToString(),
                confidence,
                digit == default ? null : digit - '0',
                $"{FormatSymbols(recognition.Symbols)}; loops={topology.Count}; upper={topology.TopmostCenterYRatio:0.00}"));
            if (digit == default || confidence < 0.20f)
            {
                return;
            }

            prefix.Add(digit);
            prefixConfidence = Math.Min(prefixConfidence, confidence);
        }

        AddRecoveredReader(new string(prefix.ToArray()), prefixConfidence, "single-cell OCR");

        void AddRecoveredReader(string recoveredPrefix, float recoveryConfidence, string recoveryKind)
        {
            var recoveredText = recoveredPrefix + source.Amount.ToString(CultureInfo.InvariantCulture);
            if (!long.TryParse(recoveredText, NumberStyles.None, CultureInfo.InvariantCulture, out var recoveredAmount) ||
                recoveredAmount <= 0 || recoveredAmount > 2_500_000_000)
            {
                return;
            }

            readers.Add(new ReaderResult(
                "Leading merged-glyph recovery",
                recoveredAmount,
                recoveredText,
                Math.Min(source.Confidence, recoveryConfidence),
                $"{source.Method} + geometry-segmented leading digits",
                $"Geometry recovery ({recoveryKind})",
                source.Variant,
                source.Symbols));
        }
    }

    private static void RecoverTrailingDigitWhenSupported(
        List<ReaderResult> readers,
        IReadOnlyList<GtaAmountOcrDiagnostic> probeDiagnostics)
    {
        var baseGroup = readers
            .GroupBy(reader => reader.Amount)
            .OrderByDescending(group => group.Select(reader => reader.ReaderName).Distinct().Count())
            .FirstOrDefault();
        var representative = baseGroup?
            .Where(reader => reader.Symbols is { Count: > 0 })
            .OrderByDescending(reader => reader.Confidence)
            .FirstOrDefault();
        if (baseGroup is null || representative?.Symbols is not { Count: > 0 })
        {
            return;
        }

        var baseDigits = baseGroup.Key.ToString(CultureInfo.InvariantCulture);
        if (GetRepresentativeDigitCount([representative]) != baseDigits.Length + 1)
        {
            return;
        }

        var tailGroups = probeDiagnostics
            .Where(result => result.ParsedAmount is >= 0 and <= 9 &&
                result.RawText.Count(char.IsDigit) == 1)
            .GroupBy(result => result.ParsedAmount!.Value)
            .Select(group => new
            {
                Digit = group.Key,
                Evidence = group
                    .GroupBy(result => result.PreprocessingMethod, StringComparer.Ordinal)
                    .Select(method => method.OrderByDescending(result => result.Confidence).First())
                    .ToArray()
            })
            .OrderByDescending(group => group.Evidence.Length)
            .ToArray();
        if (tailGroups.Length == 0 || tailGroups[0].Evidence.Length < 2 ||
            tailGroups.Skip(1).Any(group => group.Evidence.Length == tailGroups[0].Evidence.Length))
        {
            return;
        }

        var recoveredText = baseDigits + tailGroups[0].Digit.ToString(CultureInfo.InvariantCulture);
        if (!long.TryParse(recoveredText, NumberStyles.None, CultureInfo.InvariantCulture, out var recoveredAmount) ||
            recoveredAmount <= 0 || recoveredAmount > 2_500_000_000)
        {
            return;
        }

        readers.RemoveAll(reader => reader.Amount == baseGroup.Key);
        for (var index = 0; index < tailGroups[0].Evidence.Length; index++)
        {
            var evidence = tailGroups[0].Evidence[index];
            readers.Add(new ReaderResult(
                $"Trailing digit recovery {index + 1}: {evidence.PreprocessingMethod}",
                recoveredAmount,
                recoveredText,
                Math.Min(representative.Confidence, evidence.Confidence),
                $"{representative.Method} + trailing digit evidence",
                "Geometry recovery",
                representative.Variant,
                index == 0 ? representative.Symbols : null));
        }
    }

    private IReadOnlyList<GtaDigitOcrDiagnostic> ReadIndividualDigits(
        ReaderResult source,
        CancellationToken cancellationToken,
        out string digits,
        out int ocrAttempts)
    {
        var symbols = source.Symbols?
            .Where(symbol => symbol.Text.Length == 1 && char.IsDigit(symbol.Text[0]))
            .OrderBy(symbol => symbol.Left)
            .ToList() ?? [];
        if (symbols.Count == 0)
        {
            digits = string.Empty;
            ocrAttempts = 0;
            return [];
        }

        var sourceImage = Decode(source.Variant);
        var typicalWidth = symbols
            .Select(symbol => symbol.Right - symbol.Left)
            .OrderBy(width => width)
            .ElementAt(symbols.Count / 2);
        var results = new List<GtaDigitOcrDiagnostic>();
        var selectedDigits = new List<char>();
        ocrAttempts = 0;
        var verificationPositions = symbols
            .Select((symbol, index) => new { Symbol = symbol, Index = index })
            .OrderBy(item => item.Symbol.Text[0] is '8' or '6' ? 0 : 1)
            .ThenBy(item => item.Symbol.Confidence)
            .Take(3)
            .Select(item => item.Index)
            .ToHashSet();
        for (var index = 0; index < symbols.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var symbol = symbols[index];
            var rawWidth = Math.Max(1, symbol.Right - symbol.Left);
            var width = Math.Min(rawWidth, Math.Max(8, (int)Math.Round(typicalWidth * 1.35)));
            var paddingX = Math.Max(3, width / 10);
            var paddingY = Math.Max(3, (symbol.Bottom - symbol.Top) / 12);
            var bounds = ClampBounds(
                sourceImage,
                new Int32Rect(
                    symbol.Left - paddingX,
                    symbol.Top - paddingY,
                    width + (paddingX * 2),
                    (symbol.Bottom - symbol.Top) + (paddingY * 2)));
            var shouldVerify = verificationPositions.Contains(index);
            OcrRecognitionResult? recognition = null;
            if (shouldVerify)
            {
                var variant = _preprocessing.CreateCharacterVariant(
                    sourceImage,
                    bounds,
                    $"Segmented digit {index + 1}");
                recognition = _amountOcr.RecognizeAmountCharacter(variant, cancellationToken);
                ocrAttempts++;
            }

            var confidence = recognition is null
                ? QuickCapturePolicy.NormalizeConfidence(symbol.Confidence)
                : QuickCapturePolicy.NormalizeConfidence(recognition.Confidence);
            var ocrDigit = recognition?.Text.FirstOrDefault(char.IsDigit) ?? default;
            var selected = ocrDigit == default ? symbol.Text[0] : ocrDigit;
            var holes = CountEnclosedRegions(Crop(sourceImage, bounds));
            var shapeSupport = DescribeShapeSupport(selected, holes);
            var shapeContradiction = selected == '8' && holes == 1 || selected == '6' && holes >= 2;
            var uncertain = shouldVerify
                ? ocrDigit == default || confidence < 0.35f || shapeContradiction
                : confidence < 0.35f || shapeContradiction;
            selectedDigits.Add(selected);
            results.Add(new GtaDigitOcrDiagnostic(
                index + 1,
                bounds,
                recognition is null
                    ? $"Whole-line symbol retained: {symbol.Text}"
                    : string.IsNullOrWhiteSpace(recognition.Text)
                        ? $"Tesseract: none; whole-line symbol: {symbol.Text}"
                        : $"Tesseract: {recognition.Text}; whole-line symbol: {symbol.Text}",
                selected,
                confidence,
                holes,
                shapeSupport,
                uncertain));
        }

        digits = new string(selectedDigits.ToArray());
        return results;
    }

    private PreprocessedImage SelectVariant(BitmapSource source, VariantKind kind)
    {
        return kind switch
        {
            VariantKind.LocalContrast => _preprocessing.CreateLocalContrastVariant(source),
            VariantKind.GreenSuppression => _preprocessing.CreateGreenSuppressionVariant(source),
            VariantKind.ClosedMask => _preprocessing.CreateClosedTextVariant(source),
            VariantKind.NeutralBrightness => _preprocessing.CreateNeutralBrightnessVariant(source),
            _ => _preprocessing.CreateLowSaturationVariant(source)
        };
    }

    private static List<BitmapSource> SelectStableFrames(
        IReadOnlyList<BitmapSource> frames,
        CancellationToken cancellationToken)
    {
        var stable = new List<BitmapSource> { frames[0] };
        for (var index = 1; index < frames.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CalculateFrameDifference(frames[0], frames[index]) <= MaximumStableFrameDifference)
            {
                stable.Add(frames[index]);
            }
        }

        return stable;
    }

    public static double CalculateFrameDifference(BitmapSource left, BitmapSource right)
    {
        if (left.PixelWidth != right.PixelWidth || left.PixelHeight != right.PixelHeight)
        {
            return 100;
        }

        var leftPixels = CopyBgra(left);
        var rightPixels = CopyBgra(right);
        var stepX = Math.Max(1, left.PixelWidth / 96);
        var stepY = Math.Max(1, left.PixelHeight / 54);
        long total = 0;
        long count = 0;
        for (var y = 0; y < left.PixelHeight; y += stepY)
        {
            for (var x = 0; x < left.PixelWidth; x += stepX)
            {
                var offset = ((y * left.PixelWidth) + x) * 4;
                var leftLuma = Luminance(leftPixels, offset);
                var rightLuma = Luminance(rightPixels, offset);
                total += Math.Abs(leftLuma - rightLuma);
                count++;
            }
        }

        return count == 0 ? 100 : total * 100d / (count * 255d);
    }

    private static BitmapSource CreateMedianLuminanceComposite(IReadOnlyList<BitmapSource> frames)
    {
        var buffers = frames.Select(CopyBgra).ToArray();
        var width = frames[0].PixelWidth;
        var height = frames[0].PixelHeight;
        var output = new byte[checked(width * height * 4)];
        var values = new byte[buffers.Length];
        for (var pixel = 0; pixel < width * height; pixel++)
        {
            var offset = pixel * 4;
            for (var frame = 0; frame < buffers.Length; frame++)
            {
                values[frame] = Luminance(buffers[frame], offset);
            }

            Array.Sort(values);
            var median = values[values.Length / 2];
            SetGray(output, offset, median);
        }

        return CreateBitmap(width, height, output);
    }

    private static BitmapSource CreateBrightLowSaturationComposite(IReadOnlyList<BitmapSource> frames)
    {
        var buffers = frames.Select(CopyBgra).ToArray();
        var width = frames[0].PixelWidth;
        var height = frames[0].PixelHeight;
        var output = new byte[checked(width * height * 4)];
        for (var pixel = 0; pixel < width * height; pixel++)
        {
            var offset = pixel * 4;
            byte best = 0;
            foreach (var buffer in buffers)
            {
                var minimum = Math.Min(buffer[offset], Math.Min(buffer[offset + 1], buffer[offset + 2]));
                var maximum = Math.Max(buffer[offset], Math.Max(buffer[offset + 1], buffer[offset + 2]));
                if (maximum - minimum <= 112)
                {
                    best = Math.Max(best, Luminance(buffer, offset));
                }
            }

            SetGray(output, offset, best);
        }

        return CreateBitmap(width, height, output);
    }

    private BitmapSource CreateLocalContrastComposite(BitmapSource medianComposite)
    {
        return Decode(_preprocessing.CreateLocalContrastVariant(medianComposite));
    }

    private static int CountEnclosedRegions(BitmapSource source)
    {
        return AnalyzeEnclosedRegions(source).Count;
    }

    private static EnclosedRegionAnalysis AnalyzeEnclosedRegions(BitmapSource source)
    {
        var pixels = CopyBgra(source);
        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var visited = new bool[width * height];
        var queue = new Queue<int>();
        var holes = 0;
        double? topmostCenterYRatio = null;
        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var start = (y * width) + x;
                if (visited[start] || Luminance(pixels, start * 4) > 96)
                {
                    continue;
                }

                visited[start] = true;
                queue.Enqueue(start);
                var touchesBorder = false;
                var area = 0;
                long totalY = 0;
                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    var currentX = current % width;
                    var currentY = current / width;
                    area++;
                    totalY += currentY;
                    if (currentX == 0 || currentY == 0 || currentX == width - 1 || currentY == height - 1)
                    {
                        touchesBorder = true;
                    }

                    var neighbors = new[]
                    {
                        currentX > 0 ? current - 1 : -1,
                        currentX + 1 < width ? current + 1 : -1,
                        currentY > 0 ? current - width : -1,
                        currentY + 1 < height ? current + width : -1
                    };
                    foreach (var next in neighbors)
                    {
                        if (next < 0 || next >= visited.Length || visited[next] ||
                            Luminance(pixels, next * 4) > 96)
                        {
                            continue;
                        }

                        visited[next] = true;
                        queue.Enqueue(next);
                    }
                }

                if (!touchesBorder && area >= Math.Max(6, width * height / 250))
                {
                    holes++;
                    var centerYRatio = totalY / (double)area / Math.Max(1, height - 1);
                    topmostCenterYRatio = topmostCenterYRatio is null
                        ? centerYRatio
                        : Math.Min(topmostCenterYRatio.Value, centerYRatio);
                }
            }
        }

        return new EnclosedRegionAnalysis(Math.Min(holes, 9), topmostCenterYRatio);
    }

    private static string DescribeShapeSupport(char digit, int holes)
    {
        return digit switch
        {
            '8' when holes >= 2 => "Two or more enclosed regions support 8.",
            '8' => "Topology is incomplete for 8; treated only as a supporting vote.",
            '6' when holes == 1 => "One enclosed region supports 6.",
            '6' when holes >= 2 => "Multiple enclosed regions conflict with 6.",
            '0' when holes == 1 => "One enclosed region supports 0.",
            '1' or '3' when holes == 0 => $"No enclosed region supports {digit}.",
            _ => $"Observed {holes} enclosed region(s); no automatic substitution was made."
        };
    }

    private static bool IsValidCrop(Int32Rect bounds, BitmapSource source)
    {
        return bounds.X >= 0 && bounds.Y >= 0 && bounds.Width > 0 && bounds.Height > 0 &&
            bounds.X + bounds.Width <= source.PixelWidth && bounds.Y + bounds.Height <= source.PixelHeight;
    }

    private static Int32Rect ExpandAmountCropForLeadingGlyphs(
        GtaResultScreenDiagnostics diagnostics,
        BitmapSource source)
    {
        if (diagnostics.AnchorBounds is not Int32Rect anchor)
        {
            return diagnostics.AmountCropBounds;
        }

        var original = diagnostics.AmountCropBounds;
        var left = Math.Clamp(
            anchor.X - (int)Math.Round(anchor.Height * 0.25),
            0,
            original.X);
        var right = Math.Min(source.PixelWidth, original.X + original.Width);
        return new Int32Rect(left, original.Y, Math.Max(1, right - left), original.Height);
    }

    private static Int32Rect ClampBounds(BitmapSource source, Int32Rect bounds)
    {
        var x = Math.Clamp(bounds.X, 0, source.PixelWidth - 1);
        var y = Math.Clamp(bounds.Y, 0, source.PixelHeight - 1);
        var right = Math.Clamp(bounds.X + bounds.Width, x + 1, source.PixelWidth);
        var bottom = Math.Clamp(bounds.Y + bounds.Height, y + 1, source.PixelHeight);
        return new Int32Rect(x, y, right - x, bottom - y);
    }

    private static CroppedBitmap Crop(BitmapSource source, Int32Rect bounds)
    {
        var crop = new CroppedBitmap(source, bounds);
        crop.Freeze();
        return crop;
    }

    private static BitmapSource Decode(PreprocessedImage image)
    {
        return GtaAmountPreprocessingService.DecodePreview(image);
    }

    private static byte[] CopyBgra(BitmapSource source)
    {
        BitmapSource converted = source;
        if (source.Format != PixelFormats.Bgra32)
        {
            converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
        }

        var pixels = new byte[checked(converted.PixelWidth * converted.PixelHeight * 4)];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        return pixels;
    }

    private static byte Luminance(byte[] pixels, int offset)
    {
        return (byte)Math.Clamp(
            (int)Math.Round((pixels[offset + 2] * 0.299) +
                (pixels[offset + 1] * 0.587) +
                (pixels[offset] * 0.114)),
            0,
            255);
    }

    private static void SetGray(byte[] pixels, int offset, byte value)
    {
        pixels[offset] = value;
        pixels[offset + 1] = value;
        pixels[offset + 2] = value;
        pixels[offset + 3] = 255;
    }

    private static BitmapSource CreateBitmap(int width, int height, byte[] pixels)
    {
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        source.Freeze();
        return source;
    }

    private static string FormatSymbols(IReadOnlyList<OcrSymbol>? symbols)
    {
        return symbols is not { Count: > 0 }
            ? string.Empty
            : string.Join(" ", symbols.Select(symbol =>
                $"{symbol.Text}@{symbol.Left},{symbol.Top}:{symbol.Right - symbol.Left}x{symbol.Bottom - symbol.Top}"));
    }

    private enum VariantKind
    {
        LowSaturation,
        ClosedMask,
        NeutralBrightness,
        GreenSuppression,
        LocalContrast
    }

    private sealed record ReaderResult(
        string ReaderName,
        long Amount,
        string RawText,
        float Confidence,
        string Method,
        string PageSegmentationMode,
        PreprocessedImage Variant,
        IReadOnlyList<OcrSymbol>? Symbols);

    private sealed record EnclosedRegionAnalysis(int Count, double? TopmostCenterYRatio);
}
