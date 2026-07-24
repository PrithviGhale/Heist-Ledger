namespace GTAEarningsTracker.Models;

public sealed class EarningRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public decimal Amount { get; init; }

    public DateTime TimestampUtc { get; init; }

    public string? Source { get; init; }
}
