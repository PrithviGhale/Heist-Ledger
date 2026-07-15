namespace GTAEarningsTracker.Models;

public sealed class DiscordWebhookSettings
{
    public string Username { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    public bool IncludeQuickScanScreenshot { get; set; } = true;

    public string ProtectedWebhookUrl { get; set; } = string.Empty;
}
