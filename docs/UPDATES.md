# In-app updates

Heist Ledger uses Velopack with the public [Heist-Ledger-Updates](https://github.com/PrithviGhale/Heist-Ledger-Updates) release repository starting with v1.5.1. The main Heist-Ledger repository is the human-facing download page. No server subscription, account, or embedded access token is required. The `win` channel accepts stable releases and does not downgrade the installed version.

## Using updates

Install `HeistLedger-Setup.exe` once and launch Heist Ledger using its installed shortcut. Existing earnings and settings in `%LocalAppData%\GTAEarningsTracker` are reused. Close older ZIP copies; they continue to run independently and will not update themselves.

Settings > App Updates shows the installed version, optional startup checking, a manual check, plain-text release notes, and download progress. Downloading and installing are separate actions. A completed download is retained for a later restart, but nothing is automatically applied on launch. Install & Restart runs the normal WPF shutdown cleanup and refuses to interrupt a scan or unconfirmed payout.

Open Settings with the header gear; use Back to Home to return. When a check discovers a newer version, the gear displays a static `!` badge and Settings shows an update status pill. The known version is saved locally so the badge survives an offline launch. Installing that version, or a successful check reporting no newer release, clears it. This is an availability indicator rather than an unread-message badge: merely opening Settings does not dismiss it.

Settings includes shortcuts to Updates, Profile, Style, Quick Scan, and Discord. Mouse-wheel and section navigation use brief scrolling animations that release their clocks when finished. Scrollbar dragging, keyboard navigation, nested text/notes areas, and reduced-animation preferences retain their normal behavior. The badge does not pulse and the page adds no polling timer or continuous animation.

When internet access fails, the app stays usable. Retry in Settings later. Cancelled, incomplete, or checksum-invalid packages are never offered for installation. GitHub checks use the public API and are subject to shared-IP rate limits, so startup checks occur only once per app launch; there is no polling loop.

## Building the installer

```powershell
powershell -NoProfile -File scripts\Build-UpdaterRelease.ps1
```

The script restores the pinned local `vpk` tool, publishes a self-contained Windows x64 directory, and packages the app with its icon and matching versioned release notes. It creates:

- `HeistLedger-win-Setup.exe` for the first installation.
- `HeistLedger-<version>-full.nupkg` for verified update downloads.
- `releases.win.json`, the update feed, and Velopack metadata.

Files are written to `artifacts/updater-release/Releases`; nothing is uploaded by the script. The separate install directory is `%LocalAppData%\HeistLedger`, leaving user data in `%LocalAppData%\GTAEarningsTracker`. The installer includes .NET and declares the required Visual C++ runtime. Code signing is not configured.

To rebuild the same version locally, pass a fresh `-OutputDirectory artifacts/updater-rebuild`. The script never overwrites an existing package of the same version.

For later releases, use `-DownloadPrevious` to retrieve the previous stable update package before packaging, allowing delta packages to be generated. Do not use that switch for the first updater release (v1.3.0 has no Velopack feed). Keep the package ID `HeistLedger` and channel `win` stable.

## Publishing

Bump the project version, write matching release notes, run the tests, and rebuild the installer. Upload all generated Velopack packages and metadata together to a stable release in **Heist-Ledger-Updates**. Publish that feed only after uploads finish. Copy the generated `HeistLedger-win-Setup.exe` to `HeistLedger-Setup.exe` and upload ONLY that installer to the matching main **Heist-Ledger** release. GitHub adds Source code archives automatically; they cannot be removed. Keep old update releases available for delta updates.

### v1.5.1 migration compatibility

v1.4.x and v1.5.0 installations still query the main repository. Its older v1.5.0 release retains a compatibility `releases.win.json` and the v1.5.1 full package, which switches them to the new update repository after installation. Do not delete those compatibility assets. The pinned Velopack GitHub source searches only the ten most recently published releases, so keep that compatibility release within this list while supporting old clients; before publishing enough releases to displace it, provide a renewed stable compatibility release (not a prerelease) or explicitly require old clients to install the latest Setup once. Do not remove the bridge merely because the latest release has a single installer.

Verify both live paths after publishing with `AppUpdateTests --github` (new feed) and `AppUpdateTests --github-legacy` (old-client compatibility feed). These download and verify packages without installing the app.

## Local verification

```powershell
dotnet run --project Tests\AppUpdateTests -c Release -- artifacts\updater-release\Releases
```

Tests use temporary caches and Velopack's test locator. They exercise checking, notes, verified download, cancellation, corruption, equal/newer installed versions, unavailable sources, old settings, and the actual generated release package. They do not install software, restart the app, or publish a release. A real installed upgrade/restart should also be tried before public release.

References: [Velopack integration](https://docs.velopack.io/integrating/overview), [update sources](https://docs.velopack.io/integrating/update-sources), [testing](https://docs.velopack.io/integrating/testing).
