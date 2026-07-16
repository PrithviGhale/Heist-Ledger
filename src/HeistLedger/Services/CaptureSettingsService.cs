using System.IO;
using System.Text.Json;
using GTAEarningsTracker.Models;

namespace GTAEarningsTracker.Services;

public sealed class CaptureSettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };
    private readonly SemaphoreSlim _saveSemaphore = new(1, 1);

    public CaptureSettingsService()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        SettingsFilePath = Path.Combine(localAppData, "GTAEarningsTracker", "settings.json");
    }

    public string SettingsFilePath { get; }

    public async Task<CaptureSettings> LoadAsync()
    {
        if (!File.Exists(SettingsFilePath))
        {
            return new CaptureSettings();
        }

        try
        {
            await using var stream = File.OpenRead(SettingsFilePath);
            var settings = await JsonSerializer.DeserializeAsync<CaptureSettings>(stream, SerializerOptions)
                ?? new CaptureSettings();
            return Migrate(settings);
        }
        catch (JsonException)
        {
            return new CaptureSettings();
        }
        catch (IOException)
        {
            return new CaptureSettings();
        }
        catch (UnauthorizedAccessException)
        {
            return new CaptureSettings();
        }
    }

    public static CaptureSettings Migrate(CaptureSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.AreaMode = ScannerAreaMode.SelectedRegion;
        settings.RequireQuickCaptureConfirmation = true;
        return settings;
    }

    public async Task SaveAsync(CaptureSettings settings)
    {
        await _saveSemaphore.WaitAsync();
        try
        {
        var directory = Path.GetDirectoryName(SettingsFilePath)
            ?? throw new InvalidOperationException("The capture settings directory is unavailable.");

        Directory.CreateDirectory(directory);

        var temporaryPath = SettingsFilePath + ".tmp";
        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions);
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
        finally
        {
            _saveSemaphore.Release();
        }
    }
}
