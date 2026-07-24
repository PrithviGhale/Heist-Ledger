using GTAEarningsTracker.Models;

namespace GTAEarningsTracker.Services;

public sealed record EarningsSession(
    decimal Total,
    DateTime? StartUtc,
    DateTime? EndUtc)
{
    public bool IsActive => StartUtc is not null && EndUtc is not null;
}

public sealed class EarningsSessionService
{
    public EarningsSession GetCurrentSession(
        IEnumerable<EarningRecord> earnings,
        TimeSpan duration,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(earnings);
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        DateTime? sessionStartUtc = null;
        decimal sessionTotal = 0;

        foreach (var record in earnings
                     .Where(record => record.TimestampUtc <= nowUtc)
                     .OrderBy(record => record.TimestampUtc))
        {
            if (sessionStartUtc is null || record.TimestampUtc >= sessionStartUtc.Value.Add(duration))
            {
                sessionStartUtc = record.TimestampUtc;
                sessionTotal = record.Amount;
            }
            else
            {
                sessionTotal += record.Amount;
            }
        }

        if (sessionStartUtc is null)
        {
            return new EarningsSession(0, null, null);
        }

        var sessionEndUtc = sessionStartUtc.Value.Add(duration);
        return nowUtc < sessionEndUtc
            ? new EarningsSession(sessionTotal, sessionStartUtc, sessionEndUtc)
            : new EarningsSession(0, null, null);
    }
}
