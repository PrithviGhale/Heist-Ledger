using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using GTAEarningsTracker.Models;

namespace GTAEarningsTracker.Services;

public sealed record DiscordWebhookResult(bool Success, string Message);

public sealed class DiscordWebhookService : IDisposable
{
    private static readonly CultureInfo MoneyCulture = CultureInfo.GetCultureInfo("en-US");
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public static bool IsValidWebhookUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        var isDiscordHost = uri.Host.Equals("discord.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".discord.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("discordapp.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".discordapp.com", StringComparison.OrdinalIgnoreCase);
        return isDiscordHost &&
            uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length >= 4;
    }

    public async Task<DiscordWebhookResult> SendPayoutAsync(
        string webhookUrl,
        DiscordPayoutNotification notification,
        CancellationToken cancellationToken)
    {
        if (!IsValidWebhookUrl(webhookUrl))
        {
            return new DiscordWebhookResult(false, "The saved Discord webhook URL is invalid.");
        }

        var safeUsername = EscapeDiscordText(notification.Username.Trim());
        var content = string.Join(
            '\n',
            $"**{safeUsername}** earned: **{FormatMoney(notification.Amount)}**",
            $"Last 24 hours: **{FormatMoney(notification.Last24Hours)}**",
            $"Last 7 days: **{FormatMoney(notification.Last7Days)}**",
            $"Method: {EscapeDiscordText(notification.Method)}",
            $"Time: {notification.TimestampUtc.ToLocalTime().ToString("h:mm tt", CultureInfo.CurrentCulture)}");

        return await SendAsync(webhookUrl, content, notification.ScreenshotPng, cancellationToken);
    }

    public Task<DiscordWebhookResult> SendTestAsync(
        string webhookUrl,
        string username,
        CancellationToken cancellationToken)
    {
        var safeUsername = EscapeDiscordText(username.Trim());
        var content = $"**{safeUsername}** connected Heist Ledger successfully. Future confirmed Quick Scan payouts will appear here.";
        return SendAsync(webhookUrl, content, null, cancellationToken);
    }

    private async Task<DiscordWebhookResult> SendAsync(
        string webhookUrl,
        string message,
        byte[]? screenshotPng,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, AddWaitParameter(webhookUrl));
            var payload = new
            {
                content = message,
                username = "Heist Ledger",
                allowed_mentions = new { parse = Array.Empty<string>() },
                attachments = screenshotPng is null
                    ? Array.Empty<object>()
                    : [new { id = 0, filename = "quick-scan-payout.png", description = "Quick Scan payout preview" }]
            };

            if (screenshotPng is null)
            {
                request.Content = new StringContent(
                    JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json");
            }
            else
            {
                var multipart = new MultipartFormDataContent();
                multipart.Add(
                    new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
                    "payload_json");
                var fileContent = new ByteArrayContent(screenshotPng);
                fileContent.Headers.ContentType = new("image/png");
                multipart.Add(fileContent, "files[0]", "quick-scan-payout.png");
                request.Content = multipart;
            }

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return new DiscordWebhookResult(true, "Sent to Discord.");
            }

            return new DiscordWebhookResult(
                false,
                $"Discord returned {(int)response.StatusCode} ({response.StatusCode}).");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DiscordWebhookResult(false, "Discord did not respond within 15 seconds.");
        }
        catch (OperationCanceledException)
        {
            return new DiscordWebhookResult(false, "Discord delivery was cancelled during shutdown.");
        }
        catch (HttpRequestException)
        {
            return new DiscordWebhookResult(false, "Discord could not be reached. Check your connection and webhook.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException)
        {
            return new DiscordWebhookResult(false, "The Discord message could not be prepared.");
        }
    }

    private static Uri AddWaitParameter(string webhookUrl)
    {
        var builder = new UriBuilder(webhookUrl);
        var query = builder.Query.TrimStart('?');
        builder.Query = string.IsNullOrWhiteSpace(query) ? "wait=true" : $"{query}&wait=true";
        return builder.Uri;
    }

    private static string EscapeDiscordText(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("*", "\\*", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("~", "\\~", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal)
            .Replace("@", "@\u200B", StringComparison.Ordinal);
    }

    private static string FormatMoney(decimal amount) =>
        amount.ToString("$#,##0.##", MoneyCulture);

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
