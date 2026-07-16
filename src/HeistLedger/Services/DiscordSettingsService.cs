using System.IO;
using System.Text.Json;
using GTAEarningsTracker.Models;

namespace GTAEarningsTracker.Services;

public sealed class DiscordSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public DiscordSettingsService()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GTAEarningsTracker");
        SettingsFilePath = Path.Combine(folder, "discord-settings.json");
    }

    public string SettingsFilePath { get; }

    public async Task<DiscordWebhookSettings> LoadAsync()
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
            {
                return new DiscordWebhookSettings();
            }

            await using var stream = File.OpenRead(SettingsFilePath);
            return await JsonSerializer.DeserializeAsync<DiscordWebhookSettings>(stream, JsonOptions) ??
                new DiscordWebhookSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new DiscordWebhookSettings();
        }
    }

    public async Task SaveAsync(DiscordWebhookSettings settings)
    {
        var directory = Path.GetDirectoryName(SettingsFilePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = SettingsFilePath + ".tmp";

        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions);
            }

            File.Move(temporaryPath, SettingsFilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
