using System.Diagnostics;
using GTAEarningsTracker.Services;
using System.Windows;

namespace GTAEarningsTracker;

public partial class MainWindow
{
    private readonly AppUpdateService _appUpdateService = new();
    private CancellationTokenSource? _updateDownloadCancellation;
    private bool _updatesBusy;
    private bool _hasAvailableUpdate;

    private void InitializeUpdatesUi()
    {
        CurrentAppVersionText.Text = $"Heist Ledger v{AppUpdateService.CurrentVersion}";
        CheckUpdatesOnStartupCheckBox.IsChecked = _captureSettings.CheckForUpdatesOnStartup;
        try
        {
            if (!_appUpdateService.IsInstalled)
                AppUpdateStatusText.Text = "Install the updater-enabled Setup once to enable in-app updates. Your earnings and settings are preserved.";
            else if (_appUpdateService.ReadyVersion is { } ready)
                ShowReadyUpdate(ready);
        }
        catch (Exception) { AppUpdateStatusText.Text = "The updater is unavailable. You can still use the app and open the release page."; }
    }

    private async void CheckUpdatesOnStartupCheckBox_Click(object sender, RoutedEventArgs e)
    {
        _captureSettings.CheckForUpdatesOnStartup = CheckUpdatesOnStartupCheckBox.IsChecked == true;
        await SaveSettingsQuietlyAsync();
    }

    private async void CheckAppUpdatesButton_Click(object sender, RoutedEventArgs e) => await CheckAppUpdatesAsync();

    private async Task CheckAppUpdatesAsync(bool automatic = false)
    {
        if (_updatesBusy || _isClosing) return;
        _updatesBusy = true;
        CheckAppUpdatesButton.IsEnabled = false;
        DownloadAppUpdateButton.IsEnabled = false;
        try
        {
            if (_appUpdateService.ReadyVersion is { } ready)
            {
                ShowReadyUpdate(ready);
                return;
            }
            if (!_appUpdateService.IsInstalled)
            {
                if (!automatic) AppUpdateStatusText.Text = "This copy runs without the updater installer. Use RELEASE PAGE to install Setup once and enable future in-app updates.";
                return;
            }
            AppUpdateStatusText.Text = "Checking GitHub for updates…";
            var update = await Task.Run(() => _appUpdateService.CheckAsync(_shutdownCancellation.Token));
            if (_isClosing) return;
            _hasAvailableUpdate = update is not null;
            UpdateAvailableBanner.Visibility = update is null ? Visibility.Collapsed : Visibility.Visible;
            DownloadAppUpdateButton.Visibility = update is null ? Visibility.Collapsed : Visibility.Visible;
            AppUpdateNotesExpander.Visibility = update is null ? Visibility.Collapsed : Visibility.Visible;
            if (update is null)
                AppUpdateStatusText.Text = $"You're up to date (v{AppUpdateService.CurrentVersion}). Checked " + DateTime.Now.ToString("t") + ".";
            else
            {
                AppUpdateStatusText.Text = $"Version {update.Version} is available. Download it when you're ready.";
                UpdateAvailableText.Text = $"Heist Ledger {update.Version} is available.";
                AppUpdateNotesText.Text = update.Notes.Length > 16_000 ? update.Notes[..16_000] : update.Notes;
                DownloadAppUpdateButton.Content = "DOWNLOAD UPDATE";
                if (_isCompactMode && _pendingDetectedPayout is null)
                    ShowCompactScanMessage($"Update {update.Version} available in Settings");
            }
        }
        catch (OperationCanceledException) when (_isClosing) { }
        catch (Exception) { if (!_isClosing) AppUpdateStatusText.Text = "Couldn't check for updates. Check your connection and try again later, or open the release page."; }
        finally
        {
            _updatesBusy = false;
            CheckAppUpdatesButton.IsEnabled = true;
            DownloadAppUpdateButton.IsEnabled = true;
        }
    }

    private void ShowReadyUpdate(string version)
    {
        AppUpdateStatusText.Text = $"Version {version} is downloaded and verified. Restart when you're ready.";
        DownloadAppUpdateButton.Content = "INSTALL & RESTART";
        DownloadAppUpdateButton.Visibility = Visibility.Visible;
        UpdateAvailableText.Text = $"Update {version} is ready to install.";
        UpdateAvailableBanner.Visibility = Visibility.Visible;
    }

    private async void DownloadAppUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updatesBusy || _isClosing) return;
        _updatesBusy = true;
        CheckAppUpdatesButton.IsEnabled = false;
        DownloadAppUpdateButton.IsEnabled = false;
        var downloading = false;
        try
        {
            if (_appUpdateService.ReadyVersion is not null)
            {
                if (_quickScanInProgress || _pendingDetectedPayout is not null || !AddEarningButton.IsEnabled)
                {
                    AppUpdateStatusText.Text = "Finish your scan and confirm or reject the payout before restarting.";
                    return;
                }
                WindowChrome.IsEnabled = false;
                // Persist preferences before starting the external updater. The
                // normal Closed handler also disposes capture/OCR and tray hooks.
                await _captureSettingsService.SaveAsync(_captureSettings);
                if (_isClosing) return;
                _appUpdateService.PrepareRestart();
                Close();
                return;
            }
            if (!_hasAvailableUpdate) return;
            _updateDownloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCancellation.Token);
            CancelAppUpdateButton.Visibility = Visibility.Visible;
            AppUpdateProgress.Value = 0;
            AppUpdateProgress.Visibility = Visibility.Visible;
            AppUpdateStatusText.Text = "Downloading update…";
            downloading = true;
            var progress = new Progress<int>(percent =>
            {
                if (_isClosing || !downloading) return;
                AppUpdateProgress.Value = percent;
                AppUpdateStatusText.Text = $"Downloading and verifying update… {percent}%";
            });
            await _appUpdateService.DownloadAsync(progress, _updateDownloadCancellation.Token);
            downloading = false;
            if (!_isClosing && _appUpdateService.ReadyVersion is { } version) ShowReadyUpdate(version);
        }
        catch (OperationCanceledException) { if (!_isClosing) AppUpdateStatusText.Text = "Download cancelled. You can try again later."; }
        catch (Exception) { if (!_isClosing) AppUpdateStatusText.Text = "Update couldn't be completed. Nothing was installed. Retry or use the release page."; }
        finally
        {
            downloading = false;
            _updateDownloadCancellation?.Dispose();
            _updateDownloadCancellation = null;
            _updatesBusy = false;
            WindowChrome.IsEnabled = true;
            CheckAppUpdatesButton.IsEnabled = true;
            DownloadAppUpdateButton.IsEnabled = true;
            CancelAppUpdateButton.Visibility = Visibility.Collapsed;
            AppUpdateProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelAppUpdateButton_Click(object sender, RoutedEventArgs e) => _updateDownloadCancellation?.Cancel();

    private async void ViewUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        await SelectTabAnimatedAsync("Settings");
        UpdatesCard.BringIntoView();
    }

    private void OpenAppReleasesButton_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(AppUpdateService.DownloadsUrl) { UseShellExecute = true }); }
        catch (Exception) { AppUpdateStatusText.Text = "Open github.com/PrithviGhale/Heist-Ledger/releases/latest in your browser."; }
    }
}
