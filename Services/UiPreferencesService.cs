using System.IO;
using System.Text.Json;
using GTAEarningsTracker.Models;

namespace GTAEarningsTracker.Services;

public sealed class UiPreferencesService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _settingsPath;

    public UiPreferencesService()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GTAEarningsTracker");
        _settingsPath = Path.Combine(folder, "ui-settings.json");
    }

    public async Task<UiPreferences> LoadAsync()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new UiPreferences();
            }

            await using var stream = File.OpenRead(_settingsPath);
            return await JsonSerializer.DeserializeAsync<UiPreferences>(stream, JsonOptions) ??
                new UiPreferences();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new UiPreferences();
        }
    }

    public async Task SaveAsync(UiPreferences preferences)
    {
        var folder = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(folder);
        await using var stream = File.Create(_settingsPath);
        await JsonSerializer.SerializeAsync(stream, preferences, JsonOptions);
    }
}
