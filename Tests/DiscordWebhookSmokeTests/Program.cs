using GTAEarningsTracker.Services;

var protectionService = new WindowsDataProtectionService();
const string sampleSecret = "https://discord.com/api/webhooks/123456789/sample-token";
var protectedSecret = protectionService.Protect(sampleSecret);

Assert(!string.IsNullOrWhiteSpace(protectedSecret), "DPAPI returned no protected data.");
Assert(protectedSecret != sampleSecret, "DPAPI did not transform the secret.");
Assert(
    protectionService.Unprotect(protectedSecret) == sampleSecret,
    "DPAPI round-trip changed the secret.");
Assert(
    DiscordWebhookService.IsValidWebhookUrl(sampleSecret),
    "A normal Discord webhook URL was rejected.");
Assert(
    !DiscordWebhookService.IsValidWebhookUrl(
        "https://discord.com.evil.example/api/webhooks/123456789/sample-token"),
    "A non-Discord host was accepted.");
Assert(
    !DiscordWebhookService.IsValidWebhookUrl(
        "http://discord.com/api/webhooks/123456789/sample-token"),
    "An insecure webhook URL was accepted.");

Console.WriteLine("Discord webhook smoke tests passed.");

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
