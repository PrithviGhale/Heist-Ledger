using GTAEarningsTracker.Models;
using GTAEarningsTracker.Services;

var service = new EarningsSessionService();
var now = new DateTime(2026, 7, 24, 15, 0, 0, DateTimeKind.Utc);

AssertSession("empty ledger", [], TimeSpan.FromHours(24), now, 0, false);
AssertSession("24-hour session accumulates", [Record(100_000, now.AddHours(-8)), Record(250_000, now.AddHours(-2))], TimeSpan.FromHours(24), now, 350_000, true);
AssertSession("expired 24-hour session resets", [Record(500_000, now.AddHours(-25))], TimeSpan.FromHours(24), now, 0, false);
AssertSession("earning at boundary starts a new session", [Record(100_000, now.AddHours(-30)), Record(700_000, now.AddHours(-6))], TimeSpan.FromHours(24), now, 700_000, true);
AssertSession("seven-day session survives app closure", [Record(1_000_000, now.AddDays(-5)), Record(300_000, now.AddDays(-1))], TimeSpan.FromDays(7), now, 1_300_000, true);
AssertSession("expired week remains history only", [Record(1_900_000, now.AddDays(-8))], TimeSpan.FromDays(7), now, 0, false);

Console.WriteLine("All 6 earnings-session tests passed.");
return;

void AssertSession(string name, IReadOnlyCollection<EarningRecord> records, TimeSpan duration, DateTime currentUtc, decimal expectedTotal, bool expectedActive)
{
    var result = service.GetCurrentSession(records, duration, currentUtc);
    if (result.Total != expectedTotal || result.IsActive != expectedActive)
    {
        throw new InvalidOperationException($"{name} failed: total={result.Total}, active={result.IsActive}.");
    }

    Console.WriteLine($"PASS: {name}");
}

static EarningRecord Record(decimal amount, DateTime timestampUtc) => new()
{
    Amount = amount,
    TimestampUtc = timestampUtc,
    Source = "Test"
};
