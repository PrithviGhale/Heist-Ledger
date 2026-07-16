namespace GTAEarningsTracker.Models;

public sealed record DiscordPayoutNotification(
    string Username,
    decimal Amount,
    decimal Last24Hours,
    decimal Last7Days,
    string Method,
    DateTime TimestampUtc,
    byte[]? ScreenshotPng);
