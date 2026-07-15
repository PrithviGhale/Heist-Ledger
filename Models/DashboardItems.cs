namespace GTAEarningsTracker.Models;

public sealed record DailyEarningsChartItem(
    string DayLabel,
    string AmountText,
    double BarHeight,
    double Opacity);

public sealed record RecentEarningItem(
    string SourceLabel,
    string AmountText,
    string LocalTimeText);
