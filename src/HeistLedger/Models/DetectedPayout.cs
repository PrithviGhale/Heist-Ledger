using System.Windows;
using System.Windows.Media.Imaging;

namespace GTAEarningsTracker.Models;

public enum PayoutDetectionMethod
{
    PlusSignPayout,
    TotalCashEarned,
    GtaTotalCashEarnedResultScreen,
    GreenCashIconPayout
}

public sealed record DetectedPayout(
    long Amount,
    string OriginalOcrText,
    string NormalizedOcrText,
    PayoutDetectionMethod DetectionMethod,
    string PreprocessingMethod,
    float Confidence,
    string ImageFingerprint,
    bool IsAmbiguous = false,
    bool? AutomaticAddRecommended = null);

public sealed record GtaAmountOcrDiagnostic(
    string PreprocessingMethod,
    string PageSegmentationMode,
    string RawText,
    string NormalizedText,
    float Confidence,
    long? ParsedAmount,
    string SymbolSummary = "");

public sealed record GtaDigitOcrDiagnostic(
    int Position,
    Int32Rect Bounds,
    string OcrVotes,
    char? SelectedDigit,
    float Confidence,
    int? EnclosedRegionCount,
    string ShapeSupport,
    bool IsUncertain);

public sealed record GtaResultScreenDiagnostics(
    bool AnchorFound,
    string AnchorOcrText,
    float AnchorConfidence,
    Int32Rect? AnchorBounds,
    Int32Rect AmountCropBounds,
    long? SelectedAmount,
    float AmountConfidence,
    string SelectedPreprocessingMethod,
    string SelectedPageSegmentationMode,
    int AgreeingVariantCount,
    string CompetingCandidates,
    bool AutomaticAddAccepted,
    string AutomaticAddReason,
    IReadOnlyList<GtaAmountOcrDiagnostic> AmountOcrResults,
    BitmapSource? HeadingCropPreview = null,
    BitmapSource? AmountCropPreview = null,
    BitmapSource? BestProcessedAmountPreview = null,
    ScannerPerformanceDiagnostics? Performance = null,
    IReadOnlyList<GtaDigitOcrDiagnostic>? DigitOcrResults = null,
    int StableFrameCount = 1,
    int IndependentReaderAgreement = 0,
    string UncertainDigitPositions = "None",
    long? TesseractAmount = null,
    long? PaddleOcrAmount = null,
    long? DigitReaderAmount = null,
    string PaddleOcrStatus = "Not run",
    double? PaddleOcrProcessingMilliseconds = null,
    bool ForcedRescan = false);

public sealed record ScannerPerformanceDiagnostics(
    string CaptureBackend,
    double FrameChangePercentage,
    double FrameStabilityMilliseconds,
    long FramesReceived,
    long FramesIgnoredAsUnchanged,
    long OcrScansPerformed,
    double ImagePreparationMilliseconds,
    double FrameChangeDetectionMilliseconds,
    double HeadingPreprocessingMilliseconds,
    double HeadingOcrMilliseconds,
    double AmountCropMilliseconds,
    double AmountPreprocessingMilliseconds,
    double AmountOcrMilliseconds,
    double FallbackMilliseconds,
    double TotalScanMilliseconds,
    string DetectionPath,
    int PreprocessingVariantsAttempted,
    string RejectionReason,
    DateTime? LastSuccessfulDetectionUtc = null);

public sealed record PayoutAnalysisResult(
    DetectedPayout? Candidate,
    string RawText,
    float? Confidence,
    string PreprocessingMethod,
    string ImageFingerprint,
    GtaResultScreenDiagnostics? GtaResultDiagnostics = null);
