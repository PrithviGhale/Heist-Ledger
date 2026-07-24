namespace GTAEarningsTracker.Models;

public sealed record DailyEarningsChartItem(
    string DayLabel,
    string AmountText,
    double BarHeight,
    double Opacity);

public sealed record RecentEarningItem(
    Guid RecordId,
    string SourceLabel,
    string AmountText,
    string LocalTimeText);

public sealed record CalendarDayItem(
    DateTime Date,
    string DayText,
    bool IsCurrentMonth,
    bool IsToday,
    bool IsSelected,
    bool HasEarnings,
    bool IsEnabled);
