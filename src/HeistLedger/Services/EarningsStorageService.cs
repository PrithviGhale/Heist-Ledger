using System.IO;
using System.Text.Json;
using GTAEarningsTracker.Models;

namespace GTAEarningsTracker.Services;

public sealed class EarningsStorageService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    public EarningsStorageService()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        DataFilePath = Path.Combine(localAppData, "GTAEarningsTracker", "earnings.json");
    }

    public string DataFilePath { get; }

    public async Task<List<EarningRecord>> LoadAsync()
    {
        if (!File.Exists(DataFilePath))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(DataFilePath);
            var records = await JsonSerializer.DeserializeAsync<List<EarningRecord>>(
                stream,
                SerializerOptions);

            return records?
                .Where(record => record.Amount > 0 && record.TimestampUtc != default)
                .ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    public async Task SaveAsync(IReadOnlyCollection<EarningRecord> records)
    {
        var directory = Path.GetDirectoryName(DataFilePath)
            ?? throw new InvalidOperationException("The earnings data directory is unavailable.");

        Directory.CreateDirectory(directory);

        var temporaryPath = DataFilePath + ".tmp";
        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, records, SerializerOptions);
            }

            File.Move(temporaryPath, DataFilePath, overwrite: true);
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
