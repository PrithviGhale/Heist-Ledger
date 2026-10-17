using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using GTAEarningsTracker.Models;
using GTAEarningsTracker.Services;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        VelopackApp.Build().SetArgs([]).SetAutoApplyOnStartup(false).Run();
        var root = Path.Combine(Path.GetTempPath(), "HeistLedger-update-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var feed = Path.Combine(root, "feed");
            Directory.CreateDirectory(feed);
            var package = Path.Combine(feed, "HeistLedger-1.5.0-full.nupkg");
            using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(zip.CreateEntry("HeistLedger.nuspec").Open());
                writer.Write("<package><metadata><id>HeistLedger</id><version>1.5.0</version><authors>Test</authors><description>Local updater test</description></metadata></package>");
            }
            var bytes = File.ReadAllBytes(package);
            var asset = new
            {
                PackageId = "HeistLedger", Version = "1.5.0", Type = "Full", FileName = Path.GetFileName(package),
                Size = bytes.Length, SHA1 = Convert.ToHexString(SHA1.HashData(bytes)), SHA256 = Convert.ToHexString(SHA256.HashData(bytes)),
                NotesMarkdown = "Test release notes"
            };
            File.WriteAllText(Path.Combine(feed, "releases.win.json"), JsonSerializer.Serialize(new { Assets = new[] { asset } }));
            AppUpdateService Service(string version, string cache) => new(() => new UpdateManager(feed,
                new UpdateOptions { ExplicitChannel = "win", AllowVersionDowngrade = false },
                new TestVelopackLocator("HeistLedger", version, Path.Combine(root, cache))));

            var service = Service("1.4.0", "download");
            var release = await service.CheckAsync(CancellationToken.None);
            Assert(release?.Version == "1.5.0" && release.Notes == "Test release notes", "new version and release notes are returned");
            Assert(service.ReadyVersion is null, "checking does not download or install");
            var progress = new InlineProgress();
            await service.DownloadAsync(progress, CancellationToken.None);
            Assert(service.ReadyVersion == "1.5.0", "verified download is ready for explicit restart");
            Assert(Directory.GetFiles(Path.Combine(root, "download"), "*.nupkg").Length == 1, "package is cached locally");
            Assert(await Service("1.5.0", "current").CheckAsync(CancellationToken.None) is null, "same version is up to date");
            Assert(await Service("1.6.0", "newer").CheckAsync(CancellationToken.None) is null, "downgrades are rejected");
            var cancel = Service("1.4.0", "cancel");
            _ = await cancel.CheckAsync(CancellationToken.None);
            await ThrowsAsync<OperationCanceledException>(() => cancel.DownloadAsync(progress, new CancellationToken(true)));
            Assert(cancel.ReadyVersion is null, "cancelled download is not installable");
            var corrupt = Service("1.4.0", "corrupt");
            _ = await corrupt.CheckAsync(CancellationToken.None);
            File.WriteAllText(package, "corrupted package");
            await ThrowsAsync<Exception>(() => corrupt.DownloadAsync(progress, CancellationToken.None));
            Assert(corrupt.ReadyVersion is null, "failed checksum cannot become a pending update");
            var offline = new AppUpdateService(() => new UpdateManager("http://127.0.0.1:1/", null,
                new TestVelopackLocator("HeistLedger", "1.4.0", Path.Combine(root, "offline"))));
            await ThrowsAsync<Exception>(() => offline.CheckAsync(CancellationToken.None));
            Console.WriteLine("PASS: unreachable update source reports a recoverable failure");
            var standalone = new AppUpdateService();
            Assert(!standalone.IsInstalled && standalone.ReadyVersion is null, "standalone builds detect that Setup is required");
            await ThrowsAsync<InvalidOperationException>(() => standalone.CheckAsync(CancellationToken.None));
            var settings = JsonSerializer.Deserialize<CaptureSettings>("{\"X\":123}")!;
            Assert(settings.CheckForUpdatesOnStartup && settings.X == 123, "old settings retain defaults and existing data");
            settings.CheckForUpdatesOnStartup = false;
            Assert(!JsonSerializer.Deserialize<CaptureSettings>(JsonSerializer.Serialize(settings))!.CheckForUpdatesOnStartup,
                "startup preference round-trips");
            if (args.Length == 1)
            {
                var locator = new TestVelopackLocator("HeistLedger", "1.4.0", Path.Combine(root, "real-package"));
                var options = new UpdateOptions { ExplicitChannel = "win", AllowVersionDowngrade = false };
                var built = new AppUpdateService(() => args[0] is "--github" or "--github-legacy"
                    ? new UpdateManager(new GithubSource(args[0] == "--github-legacy" ? AppUpdateService.RepositoryUrl : AppUpdateService.UpdateRepositoryUrl, null, false), options, locator)
                    : new UpdateManager(Path.GetFullPath(args[0]), options, locator));
                var builtRelease = await built.CheckAsync(CancellationToken.None);
                Assert(builtRelease?.Version == AppUpdateService.CurrentVersion, "generated installer feed offers the actual app version");
                await built.DownloadAsync(new InlineProgress(), CancellationToken.None);
                Assert(built.ReadyVersion == AppUpdateService.CurrentVersion, "real release package downloads and verifies using the production service");
            }
            Console.WriteLine("All app update tests passed; no app was installed, restarted, or published.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }
    private sealed class InlineProgress : IProgress<int>
    {
        public void Report(int value) => Assert(value is >= 0 and <= 100, "download progress within bounds");
    }
}
