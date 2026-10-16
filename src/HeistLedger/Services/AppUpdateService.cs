using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace GTAEarningsTracker.Services;

public sealed record AppUpdateRelease(string Version, string Notes);

public sealed class AppUpdateService
{
    public const string RepositoryUrl = "https://github.com/PrithviGhale/Heist-Ledger";
    public const string DownloadsUrl = RepositoryUrl + "/releases/latest";
    private readonly Lazy<UpdateManager> _manager;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private UpdateInfo? _available;

    public AppUpdateService() : this(() => new UpdateManager(
        new GithubSource(RepositoryUrl, accessToken: null, prerelease: false),
        new UpdateOptions { ExplicitChannel = "win", AllowVersionDowngrade = false })) { }

    // A local feed and test locator can exercise the real download/verification
    // path without installing software or touching the user's data.
    public AppUpdateService(Func<UpdateManager> managerFactory) => _manager = new(managerFactory);

    public static string CurrentVersion => typeof(AppUpdateService).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "Unknown";
    public bool IsInstalled => _manager.Value.IsInstalled;
    public string? ReadyVersion => _manager.Value.UpdatePendingRestart is { } pending &&
        pending.Version > _manager.Value.CurrentVersion ? pending.Version.ToString() : null;

    public async Task<AppUpdateRelease?> CheckAsync(CancellationToken cancellationToken)
    {
        if (!await _operation.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("An update operation is already running.");
        try
        {
            EnsureInstalled();
            var result = await _manager.Value.CheckForUpdatesAsync().WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            _available = result;
            return result is null ? null : new AppUpdateRelease(result.TargetFullRelease.Version.ToString(),
                result.TargetFullRelease.NotesMarkdown ?? "See the release page for the full update list.");
        }
        finally { _operation.Release(); }
    }

    public async Task DownloadAsync(IProgress<int> progress, CancellationToken cancellationToken)
    {
        if (!await _operation.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("An update operation is already running.");
        try
        {
            EnsureInstalled();
            var update = _available ?? throw new InvalidOperationException("Check for updates before downloading.");
            // Velopack validates the package size and hash before making it ready.
            await _manager.Value.DownloadUpdatesAsync(update, p => progress.Report(Math.Clamp(p, 0, 100)), cancellationToken);
        }
        finally { _operation.Release(); }
    }

    public void PrepareRestart()
    {
        EnsureInstalled();
        if (_operation.CurrentCount == 0 || ReadyVersion is null)
            throw new InvalidOperationException("No completed update is ready to install.");
        // Let WPF save settings and dispose capture/OCR resources before exiting.
        _manager.Value.WaitExitThenApplyUpdates(_manager.Value.UpdatePendingRestart, silent: false, restart: true);
    }

    private void EnsureInstalled()
    {
        if (!IsInstalled) throw new InvalidOperationException(
            "Install the updater-enabled Setup once to enable in-app updates. Your saved earnings stay on this device.");
    }
}
