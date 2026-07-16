using GTAEarningsTracker.Models;

namespace GTAEarningsTracker.Services;

public enum QuickCaptureDecision
{
    Reject,
    RequireConfirmation,
    AutomaticallyAdd
}

public static class QuickCapturePolicy
{
    // This page-level threshold is conservative in combination with the required
    // three-word anchor, bounded positive amount, and ambiguity rejection.
    // Tesseract typically reports 0-1, but NormalizeConfidence also accepts 0-100.
    public const float AutomaticAddConfidenceThreshold = 0.80f;

    public static QuickCaptureDecision Evaluate(
        DetectedPayout? candidate,
        bool requireManualConfirmation)
    {
        if (candidate is null ||
            candidate.DetectionMethod is not (
                PayoutDetectionMethod.TotalCashEarned or
                PayoutDetectionMethod.GtaTotalCashEarnedResultScreen) ||
            candidate.Amount <= 0 ||
            candidate.Amount > PayoutDetectionService.MaximumPayout)
        {
            return QuickCaptureDecision.Reject;
        }

        if (requireManualConfirmation ||
            candidate.AutomaticAddRecommended != true ||
            candidate.IsAmbiguous ||
            NormalizeConfidence(candidate.Confidence) < AutomaticAddConfidenceThreshold)
        {
            return QuickCaptureDecision.RequireConfirmation;
        }

        return QuickCaptureDecision.AutomaticallyAdd;
    }

    public static float NormalizeConfidence(float confidence)
    {
        var normalized = confidence > 1 ? confidence / 100f : confidence;
        return Math.Clamp(normalized, 0f, 1f);
    }
}
