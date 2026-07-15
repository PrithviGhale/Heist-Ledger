using GTAEarningsTracker.Models;

namespace GTAEarningsTracker.Services;

public sealed class PayoutDuplicateGuard
{
    // Matches the Quick Capture notification lifetime. An identical result still
    // requires two consecutive missing scans before it can be presented again.
    private static readonly TimeSpan DismissalCooldown = TimeSpan.FromSeconds(4);

    private readonly Dictionary<string, DateTime> _dismissedCandidates = [];
    private string? _visibleCandidateKey;
    private string? _visibleImageFingerprint;
    private int _consecutiveMissingScans;

    public string? LatestImageFingerprint { get; private set; }

    public long? LastDetectedAmount { get; private set; }

    public string? LastNormalizedText { get; private set; }

    public bool ShouldPresent(DetectedPayout candidate, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        RemoveExpiredCooldowns(nowUtc);

        var key = CreateKey(candidate);
        LatestImageFingerprint = candidate.ImageFingerprint;
        LastDetectedAmount = candidate.Amount;
        LastNormalizedText = candidate.NormalizedOcrText;
        _consecutiveMissingScans = 0;

        if (_visibleCandidateKey == key ||
            string.Equals(_visibleImageFingerprint, candidate.ImageFingerprint, StringComparison.Ordinal))
        {
            return false;
        }

        if (_dismissedCandidates.TryGetValue(key, out var dismissedAtUtc) &&
            nowUtc - dismissedAtUtc < DismissalCooldown)
        {
            return false;
        }

        _visibleCandidateKey = key;
        _visibleImageFingerprint = candidate.ImageFingerprint;
        return true;
    }

    public void RecordNoCandidate(string imageFingerprint)
    {
        LatestImageFingerprint = imageFingerprint;
        _consecutiveMissingScans++;
        if (_consecutiveMissingScans < 2)
        {
            return;
        }

        _visibleCandidateKey = null;
        _visibleImageFingerprint = null;
        LastDetectedAmount = null;
        LastNormalizedText = null;
    }

    public void RecordDismissal(DetectedPayout candidate, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var key = CreateKey(candidate);
        _dismissedCandidates[key] = nowUtc;
        _visibleCandidateKey = key;
        _visibleImageFingerprint = candidate.ImageFingerprint;
        _consecutiveMissingScans = 0;
        RemoveExpiredCooldowns(nowUtc);
    }

    private void RemoveExpiredCooldowns(DateTime nowUtc)
    {
        var expiredKeys = _dismissedCandidates
            .Where(pair => nowUtc - pair.Value >= DismissalCooldown)
            .Select(pair => pair.Key)
            .ToList();

        foreach (var key in expiredKeys)
        {
            _dismissedCandidates.Remove(key);
        }
    }

    private static string CreateKey(DetectedPayout candidate)
    {
        return $"{candidate.Amount}|{candidate.NormalizedOcrText}";
    }
}
