namespace GTAEarningsTracker.Models;

public sealed class EarningRecord
{
    public decimal Amount { get; init; }

    public DateTime TimestampUtc { get; init; }

    public string? Source { get; init; }
}
