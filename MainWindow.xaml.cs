using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GTAEarningsTracker.Models;
using GTAEarningsTracker.Services;
using Brush = System.Windows.Media.Brush;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace GTAEarningsTracker;

public partial class MainWindow : Window
{
    public const double CompactWindowWidth = 420;
    public const double CompactNormalHeight = 340;
    public const double CompactPendingHeight = 460;

    private static readonly CultureInfo MoneyCulture = CultureInfo.GetCultureInfo("en-US");
    private static readonly NumberStyles AcceptedAmountStyles =
        NumberStyles.AllowCurrencySymbol |
        NumberStyles.AllowThousands |
        NumberStyles.AllowDecimalPoint |
        NumberStyles.AllowLeadingWhite |
        NumberStyles.AllowTrailingWhite;

    private readonly EarningsStorageService _storageService = new();
    private readonly CaptureSettingsService _captureSettingsService = new();
    private readonly UiPreferencesService _uiPreferencesService = new();
    private readonly UiThemeService _uiThemeService = new();
    private readonly DiscordSettingsService _discordSettingsService = new();
    private readonly WindowsDataProtectionService _windowsDataProtectionService = new();
    private readonly DiscordWebhookService _discordWebhookService = new();
    private readonly ScreenCaptureService _screenCaptureService = new();
    private readonly ImagePreprocessingService _imagePreprocessingService = new();
    private readonly OcrService _ocrService = new();
    private readonly OcrService _headingOcrService = new(OcrWorkerKind.Heading);
    private readonly OcrService _amountOcrService = new(OcrWorkerKind.Amount);
    private readonly PayoutDetectionService _payoutDetectionService;
    private readonly GtaLargeDigitReaderService _largeDigitReaderService;
    private readonly ContinuousScreenCaptureService _continuousCaptureService;
    private readonly BackgroundScannerService _backgroundScannerService;
    private readonly PayoutDuplicateGuard _duplicateGuard = new();
    private readonly SemaphoreSlim _ocrScanSemaphore = new(1, 1);
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _livePreviewTimer;
    private readonly DispatcherTimer _quickCaptureNotificationTimer;
    private readonly DispatcherTimer _settingsSaveTimer;
    private readonly object _captureExclusionLock = new();

    private List<EarningRecord> _earnings = [];
    private CaptureSettings _captureSettings = new();
    private UiPreferences _uiPreferences = new();
    private DiscordWebhookSettings _discordSettings = new();
    private string _discordWebhookUrl = string.Empty;
    private string? _discordSettingsLoadWarning;
    private CancellationTokenSource? _automaticDetectionCancellation;
    private DetectedPayout? _pendingDetectedPayout;
    private string _pendingPayoutSource = "OCR";
    private byte[]? _pendingPayoutScreenshotPng;
    private GlobalHotkeyService? _globalHotkeyService;
    private TrayIconService? _trayIconService;
    private EarningRecord? _quickCaptureNotificationRecord;
    private bool _isClosing;
    private bool _isCompactMode;
    private bool _scannerPulseRunning;
    private bool _isLoaded;
    private bool _isRecordingHotkey;
    private bool _isSelectingCaptureArea;
    private bool _isModeTransitioning;
    private bool _isTabTransitioning;
    private bool _resumeLivePreviewAfterShow;
    private bool _resumeScannerAfterShow;
    private bool _suppressSettingsUiEvents;
    private string _selectedTab = "Dashboard";
    private GtaResultScreenDiagnostics? _latestGtaDiagnostics;
    private Int32Rect? _windowPhysicalBounds;
    private bool _windowVisibleForCapture = true;
    private GlobalHotkeyModifiers _proposedHotkeyModifiers =
        GlobalHotkeyModifiers.Control | GlobalHotkeyModifiers.Shift;
    private uint _proposedHotkeyVirtualKey = 0x48;
    private string _proposedHotkeyText = "Ctrl + Shift + H";

    public MainWindow()
    {
        InitializeComponent();
        UpdateAmountPlaceholder();

        _payoutDetectionService = new PayoutDetectionService(
            _imagePreprocessingService,
            _ocrService,
            _headingOcrService,
            _amountOcrService);
        _largeDigitReaderService = new GtaLargeDigitReaderService(
            _payoutDetectionService,
            _amountOcrService);
        _continuousCaptureService = new ContinuousScreenCaptureService(_screenCaptureService);
        _backgroundScannerService = new BackgroundScannerService(
            _continuousCaptureService,
            _payoutDetectionService,
            _largeDigitReaderService);
        _backgroundScannerService.FrameAvailable += BackgroundScannerService_FrameAvailable;
        _backgroundScannerService.AnalysisCompleted += BackgroundScannerService_AnalysisCompleted;
        _backgroundScannerService.SnapshotChanged += BackgroundScannerService_SnapshotChanged;

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(1)
        };
        _refreshTimer.Tick += (_, _) => RefreshTotals();

        _livePreviewTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _livePreviewTimer.Tick += LivePreviewTimer_Tick;

        _quickCaptureNotificationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(4)
        };
        _quickCaptureNotificationTimer.Tick += (_, _) =>
        {
            HideQuickCaptureNotification();
            HideCompactScanMessage();
        };

        _settingsSaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(650)
        };
        _settingsSaveTimer.Tick += async (_, _) =>
        {
            _settingsSaveTimer.Stop();
            await SaveSettingsQuietlyAsync();
        };
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var windowHandle = new WindowInteropHelper(this).Handle;
        _globalHotkeyService = new GlobalHotkeyService(windowHandle);
        _globalHotkeyService.HotkeyPressed += GlobalHotkeyService_HotkeyPressed;
        _trayIconService = new TrayIconService();
        _trayIconService.ShowRequested += TrayIconService_ShowRequested;
        _trayIconService.ExitRequested += TrayIconService_ExitRequested;
        UpdateWindowCaptureBounds();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _earnings = await _storageService.LoadAsync();
        var savedCaptureSettings = await _captureSettingsService.LoadAsync();
        _uiPreferences = await _uiPreferencesService.LoadAsync();
        _discordSettings = await _discordSettingsService.LoadAsync();
        try
        {
            _discordWebhookUrl = _windowsDataProtectionService.Unprotect(
                _discordSettings.ProtectedWebhookUrl);
        }
        catch (Exception ex) when (ex is FormatException or Win32Exception)
        {
            _discordWebhookUrl = string.Empty;
            _discordSettings.Enabled = false;
            _discordSettingsLoadWarning =
                "The saved webhook could not be unlocked for this Windows account. Enter it again.";
        }
        _uiPreferences.Theme = _uiThemeService.Apply(_uiPreferences.Theme);
        savedCaptureSettings.AreaMode = ScannerAreaMode.SelectedRegion;
        savedCaptureSettings.RequireQuickCaptureConfirmation = true;
        var hadSavedCaptureArea = savedCaptureSettings.HasCaptureArea;
        var hadConfirmedSavedArea = savedCaptureSettings.CaptureAreaConfirmed;
        var hasValidSavedArea = savedCaptureSettings.CaptureAreaConfirmed &&
            _screenCaptureService.IsValidCaptureArea(savedCaptureSettings);
        _captureSettings = savedCaptureSettings;
        if (!hasValidSavedArea)
        {
            _captureSettings.X = 0;
            _captureSettings.Y = 0;
            _captureSettings.Width = 0;
            _captureSettings.Height = 0;
            _captureSettings.CaptureAreaConfirmed = false;
        }

        if (!hasValidSavedArea && hadSavedCaptureArea && hadConfirmedSavedArea)
        {
            CaptureStatusText.Text = "Saved capture coordinates were invalid and were not restored.";
        }

        Topmost = _captureSettings.AlwaysOnTop;
        RestoreWindowPosition();
        LoadSettingsControls();
        SelectTab(
            _captureSettings.LastSelectedTab.Equals("Settings", StringComparison.OrdinalIgnoreCase)
                ? "Settings"
                : "Dashboard",
            saveSelection: false);
        ApplyWindowMode(_captureSettings.IsCompactMode);
        UpdateCaptureAreaDisplay();
        RefreshTotals();
        UpdateDashboardStatus();
        RegisterConfiguredHotkey();
        _refreshTimer.Start();
        _isLoaded = true;
        AutomaticDetectionCheckBox.IsChecked = false;
        UpdateWindowCaptureBounds();
        if (!hasValidSavedArea)
        {
            ShowCaptureSetupState();
        }
        else
        {
            ShowNormalInterface();
        }

        if (!_isCompactMode)
        {
            _ = Dispatcher.BeginInvoke(
                () => GetSelectedScrollViewer().ScrollToTop(),
                DispatcherPriority.ContextIdle);
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed &&
            e.ClickCount == 1 &&
            (e.OriginalSource is not DependencyObject source || FindVisualParent<System.Windows.Controls.Button>(source) is null))
        {
            DragMove();
        }
    }

    private async void DashboardTabButton_Click(object sender, RoutedEventArgs e)
    {
        await SelectTabAnimatedAsync("Dashboard");
        await SaveSettingsQuietlyAsync();
    }

    private async void SettingsTabButton_Click(object sender, RoutedEventArgs e)
    {
        await SelectTabAnimatedAsync("Settings");
        await SaveSettingsQuietlyAsync();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void CompactModeButton_Click(object sender, RoutedEventArgs e)
    {
        await SetWindowModeAsync(isCompactMode: true);
    }

    private async void ExpandedModeButton_Click(object sender, RoutedEventArgs e)
    {
        await SetWindowModeAsync(isCompactMode: false);
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _isClosing = true;
        RememberCurrentWindowPosition();
        _refreshTimer.Stop();
        _livePreviewTimer.Stop();
        _quickCaptureNotificationTimer.Stop();
        _settingsSaveTimer.Stop();
        _automaticDetectionCancellation?.Cancel();
        _shutdownCancellation.Cancel();

        try
        {
            _backgroundScannerService.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            // Scanner shutdown must not prevent the process from exiting.
        }

        try
        {
            Task.Run(() => _captureSettingsService.SaveAsync(_captureSettings))
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            // Shutdown must not be blocked by a settings persistence failure.
        }

        if (_globalHotkeyService is not null)
        {
            _globalHotkeyService.HotkeyPressed -= GlobalHotkeyService_HotkeyPressed;
            _globalHotkeyService.Dispose();
            _globalHotkeyService = null;
        }

        if (_trayIconService is not null)
        {
            _trayIconService.ShowRequested -= TrayIconService_ShowRequested;
            _trayIconService.ExitRequested -= TrayIconService_ExitRequested;
            _trayIconService.Dispose();
            _trayIconService = null;
        }

        try
        {
            _payoutDetectionService.Dispose();
        }
        catch
        {
            // Application shutdown must continue even if the native OCR runtime is already unloading.
        }

        _discordWebhookService.Dispose();
        _automaticDetectionCancellation?.Dispose();
        _shutdownCancellation.Dispose();
    }

    private async void AddEarningButton_Click(object sender, RoutedEventArgs e)
    {
        await AddEarningAsync();
    }

    private async void AmountTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await AddEarningAsync();
    }

    private void AmountTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        UpdateAmountPlaceholder();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_isRecordingHotkey)
        {
            RecordProposedHotkey(e);
            return;
        }

        if (e.Key != Key.Escape)
        {
            return;
        }

        if (!_selectedTab.Equals("Dashboard", StringComparison.Ordinal))
        {
            return;
        }

        AmountTextBox.Clear();
        ValidationText.Text = string.Empty;
        AmountTextBox.Focus();
        e.Handled = true;
    }

    private async void SelectCaptureAreaButton_Click(object sender, RoutedEventArgs e)
    {
        LivePreviewCheckBox.IsChecked = false;
        AutomaticDetectionCheckBox.IsChecked = false;
        CaptureStatusText.Text = string.Empty;

        bool? selectionResult = null;
        CaptureSelectionWindow? selectionWindow = null;

        try
        {
            selectionWindow = new CaptureSelectionWindow(_screenCaptureService);
            _isSelectingCaptureArea = true;
            Hide();
            selectionResult = selectionWindow.ShowDialog();
        }
        catch (Exception)
        {
            CaptureStatusText.Text = "Capture failed.";
        }
        finally
        {
            _isSelectingCaptureArea = false;
            Show();
            Activate();
        }

        if (selectionResult != true || selectionWindow?.SelectedArea is null)
        {
            if (CaptureStatusText.Text.Length == 0)
            {
                CaptureStatusText.Text = "Selection cancelled.";
            }

            if (!HasConfirmedCaptureArea())
            {
                ShowCaptureSetupState();
            }

            return;
        }

        _captureSettings.X = selectionWindow.SelectedArea.X;
        _captureSettings.Y = selectionWindow.SelectedArea.Y;
        _captureSettings.Width = selectionWindow.SelectedArea.Width;
        _captureSettings.Height = selectionWindow.SelectedArea.Height;
        _captureSettings.AreaMode = ScannerAreaMode.SelectedRegion;
        _captureSettings.CaptureAreaConfirmed = true;
        _captureSettings.RequireQuickCaptureConfirmation = true;
        CapturePreviewImage.Source = null;
        CapturePreviewPlaceholderText.Visibility = Visibility.Visible;
        UpdateCaptureAreaDisplay();

        try
        {
            await _captureSettingsService.SaveAsync(_captureSettings);
            CaptureStatusText.Text = "Capture area selected.";
            SetupPreviewImage.Source = _screenCaptureService.Capture(_captureSettings);
            SetupPreviewBorder.Visibility = Visibility.Visible;
            SetupStatusText.Text = "Capture area ready";
            await Task.Delay(450);
            ShowNormalInterface();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _captureSettings.CaptureAreaConfirmed = false;
            CaptureStatusText.Text = "Capture area selected, but the settings file could not be saved.";
            SetupStatusText.Text = "The area could not be saved. Select it again to enable Quick Scan.";
            ShowCaptureSetupState();
        }
    }

    private void TestCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        CaptureSelectedArea(showSuccessStatus: true);
    }

    private void LivePreviewCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        if (!HasConfirmedCaptureArea())
        {
            CaptureStatusText.Text = "No capture area selected.";
            LivePreviewCheckBox.IsChecked = false;
            return;
        }

        CaptureSelectedArea(showSuccessStatus: true);
        if (HasConfirmedCaptureArea() &&
            AutomaticDetectionCheckBox.IsChecked != true)
        {
            _livePreviewTimer.Start();
        }

        UpdateDashboardStatus();
    }

    private void LivePreviewCheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        _livePreviewTimer.Stop();
        UpdateDashboardStatus();
    }

    private async void ClearCaptureAreaButton_Click(object sender, RoutedEventArgs e)
    {
        LivePreviewCheckBox.IsChecked = false;
        AutomaticDetectionCheckBox.IsChecked = false;
        _captureSettings.X = 0;
        _captureSettings.Y = 0;
        _captureSettings.Width = 0;
        _captureSettings.Height = 0;
        _captureSettings.CaptureAreaConfirmed = false;
        CapturePreviewImage.Source = null;
        CapturePreviewPlaceholderText.Visibility = Visibility.Visible;
        UpdateCaptureAreaDisplay();

        try
        {
            await _captureSettingsService.SaveAsync(_captureSettings);
            CaptureStatusText.Text = "No capture area selected.";
            ShowCaptureSetupState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CaptureStatusText.Text = "Capture failed: the cleared settings could not be saved.";
        }
    }

    private void LivePreviewTimer_Tick(object? sender, EventArgs e)
    {
        CaptureSelectedArea(showSuccessStatus: false);
    }

    private void CaptureSelectedArea(bool showSuccessStatus)
    {
        if (!HasConfirmedCaptureArea())
        {
            _livePreviewTimer.Stop();
            LivePreviewCheckBox.IsChecked = false;
            CaptureStatusText.Text = "No capture area selected.";
            return;
        }

        try
        {
            CapturePreviewImage.Source = _screenCaptureService.Capture(_captureSettings);
            CapturePreviewPlaceholderText.Visibility = Visibility.Collapsed;
            if (showSuccessStatus)
            {
                CaptureStatusText.Text = "Capture successful.";
            }
        }
        catch (Exception)
        {
            CaptureStatusText.Text = "Capture failed.";
        }
    }

    private void UpdateCaptureAreaDisplay()
    {
        var hasArea = HasConfirmedCaptureArea();
        var text = hasArea
            ? $"Capture area: X {_captureSettings.X}, Y {_captureSettings.Y}, W {_captureSettings.Width}, H {_captureSettings.Height}"
            : "Capture area: Not selected";
        CaptureAreaText.Text = text;
        SettingsCaptureAreaText.Text = hasArea
            ? $"Selected payout area: X {_captureSettings.X}, Y {_captureSettings.Y}, W {_captureSettings.Width}, H {_captureSettings.Height}"
            : "Selected payout area: Not selected";
        UpdateDashboardStatus();
    }

    private void ShowCaptureSetupState()
    {
        MinWidth = 460;
        MinHeight = 560;
        MaxWidth = 560;
        Width = 500;
        Height = 640;
        CaptureSetupPanel.Visibility = Visibility.Visible;
        ExpandedDashboard.Visibility = Visibility.Collapsed;
        CompactDashboard.Visibility = Visibility.Collapsed;
        NavigationBar.Visibility = Visibility.Collapsed;
        SetupStatusText.Text = "Select a payout area to continue.";
        SetupPreviewBorder.Visibility = Visibility.Collapsed;
        QuickCaptureButton.IsEnabled = false;
        CompactQuickCaptureButton.IsEnabled = false;
        AutomaticDetectionCheckBox.IsChecked = false;
    }

    private void ShowNormalInterface()
    {
        if (!HasConfirmedCaptureArea())
        {
            ShowCaptureSetupState();
            return;
        }

        CaptureSetupPanel.Visibility = Visibility.Collapsed;
        ApplyWindowMode(_isCompactMode);
        ExpandedDashboard.Visibility = _isCompactMode ? Visibility.Collapsed : Visibility.Visible;
        CompactDashboard.Visibility = _isCompactMode ? Visibility.Visible : Visibility.Collapsed;
        NavigationBar.Visibility = _isCompactMode ? Visibility.Collapsed : Visibility.Visible;
        QuickCaptureButton.IsEnabled = true;
        CompactQuickCaptureButton.IsEnabled = true;
    }

    private async void AnalyzeCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        await RunOcrScanAsync(_shutdownCancellation.Token);
    }

    private async void QuickCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        await RunQuickCaptureAsync(_shutdownCancellation.Token);
    }

    private async Task RunQuickCaptureAsync(CancellationToken cancellationToken)
    {
        if (!HasConfirmedCaptureArea())
        {
            ShowCaptureSetupState();
            ShowQuickCaptureStatusNotification("Select a payout area before scanning.");
            return;
        }

        SetQuickCaptureProcessing(isProcessing: true);
        var enteredScan = false;
        var wasVisible = IsVisible;
        var shouldRestoreWindow = false;
        var pausedScannerForQuickCapture = false;

        try
        {
            await _ocrScanSemaphore.WaitAsync(cancellationToken);
            enteredScan = true;
            if (AutomaticDetectionCheckBox.IsChecked == true && _backgroundScannerService.IsRunning)
            {
                _backgroundScannerService.SetPaused(paused: true);
                pausedScannerForQuickCapture = true;
            }

            var selectedBounds = new CaptureSettings
            {
                X = _captureSettings.X,
                Y = _captureSettings.Y,
                Width = _captureSettings.Width,
                Height = _captureSettings.Height
            };
            var hideForCapture = wasVisible && WindowOverlapsCaptureArea(selectedBounds);
            SetQuickScanState("Capturing…", "CAPTURING…");
            if (hideForCapture)
            {
                lock (_captureExclusionLock)
                {
                    _windowVisibleForCapture = false;
                }

                Hide();
                shouldRestoreWindow = true;
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            }

            var frames = (await Task.Run(
                () => CaptureBurst(selectedBounds, GtaLargeDigitReaderService.MaximumBurstFrames, cancellationToken),
                cancellationToken)).ToList();

            if (shouldRestoreWindow)
            {
                ShowExistingMainWindow();
                shouldRestoreWindow = false;
            }

            SetQuickScanState("Reading heading…", "ANALYZING…");
            var selectedResult = await Task.Run(
                () => _largeDigitReaderService.Analyze(
                    frames,
                    _captureSettings.DetectionMode,
                    cancellationToken,
                    forceTargetedVerification: true),
                cancellationToken);

            if (selectedResult.NeedsTargetedFallback)
            {
                SetQuickScanState("Verifying digits…", "ANALYZING…");
                if (hideForCapture)
                {
                    lock (_captureExclusionLock)
                    {
                        _windowVisibleForCapture = false;
                    }

                    Hide();
                    shouldRestoreWindow = true;
                    await Task.Delay(TimeSpan.FromMilliseconds(90), cancellationToken);
                }

                var additionalFrames = await Task.Run(
                    () => CaptureBurst(selectedBounds, 2, cancellationToken),
                    cancellationToken);
                frames.AddRange(additionalFrames);
                if (shouldRestoreWindow)
                {
                    ShowExistingMainWindow();
                    shouldRestoreWindow = false;
                }

                selectedResult = await Task.Run(
                    () => _largeDigitReaderService.Analyze(
                        frames,
                        _captureSettings.DetectionMode,
                        cancellationToken,
                        selectedResult.Analysis),
                    cancellationToken);
            }

            if (_isClosing || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var analysis = selectedResult.Analysis;
            SetQuickScanState("Reading amount…", "ANALYZING…");
            CapturePreviewImage.Source = selectedResult.PreviewFrame;
            CapturePreviewPlaceholderText.Visibility = Visibility.Collapsed;
            UpdateOcrDiagnostics(analysis);
            await HandleQuickCaptureAnalysisAsync(analysis);
        }
        catch (OperationCanceledException)
        {
            if (!_isClosing)
            {
                QuickCaptureStatusText.Text = "Quick Capture cancelled.";
                SetQuickScanState("Ready", "QUICK SCAN");
                ShowQuickCaptureStatusNotification("Quick Capture cancelled.");
            }
        }
        catch (FileNotFoundException)
        {
            QuickCaptureStatusText.Text = "OCR data is missing. Rebuild the application.";
            SetQuickScanState("Error", "QUICK SCAN");
            ShowQuickCaptureStatusNotification("OCR data is missing.");
        }
        catch (InvalidOperationException ex)
        {
            QuickCaptureStatusText.Text = ex.Message;
            SetQuickScanState("Error", "QUICK SCAN");
            ShowQuickCaptureStatusNotification("Quick Capture could not start OCR.");
        }
        catch (Exception)
        {
            QuickCaptureStatusText.Text = "Quick Capture failed while analyzing the selected area.";
            SetQuickScanState("Error", "QUICK SCAN");
            ShowQuickCaptureStatusNotification("Quick Capture failed.");
        }
        finally
        {
            if (shouldRestoreWindow && !_isClosing)
            {
                ShowExistingMainWindow();
            }

            if (enteredScan)
            {
                _ocrScanSemaphore.Release();
            }

            if (pausedScannerForQuickCapture &&
                _pendingDetectedPayout is null &&
                AutomaticDetectionCheckBox.IsChecked == true &&
                (IsVisible || _captureSettings.ContinueScannerWhileHidden))
            {
                _backgroundScannerService.SetPaused(paused: false);
            }

            if (!_isClosing)
            {
                SetQuickCaptureProcessing(isProcessing: false);
                UpdateDashboardStatus();
            }
        }
    }

    private IReadOnlyList<BitmapSource> CaptureBurst(
        CaptureSettings bounds,
        int frameCount,
        CancellationToken cancellationToken)
    {
        var frames = new List<BitmapSource>(frameCount);
        for (var index = 0; index < frameCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            frames.Add(_screenCaptureService.Capture(bounds));
            if (index + 1 < frameCount)
            {
                cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(75));
            }
        }

        return frames;
    }

    private bool WindowOverlapsCaptureArea(CaptureSettings captureArea)
    {
        lock (_captureExclusionLock)
        {
            if (!_windowVisibleForCapture || _windowPhysicalBounds is not Int32Rect windowBounds)
            {
                return false;
            }

            return windowBounds.X < captureArea.X + captureArea.Width &&
                windowBounds.X + windowBounds.Width > captureArea.X &&
                windowBounds.Y < captureArea.Y + captureArea.Height &&
                windowBounds.Y + windowBounds.Height > captureArea.Y;
        }
    }

    private async Task HandleQuickCaptureAnalysisAsync(PayoutAnalysisResult analysis)
    {
        var candidate = analysis.Candidate;
        if (candidate is null)
        {
            _duplicateGuard.RecordNoCandidate(analysis.ImageFingerprint);
            var anchorMissing = analysis.GtaResultDiagnostics?.AnchorFound != true;
            var message = anchorMissing
                ? "TOTAL CASH EARNED was not found in the selected area."
                : "No payout amount was found below TOTAL CASH EARNED.";
            SetQuickScanState("No result found", "QUICK SCAN");
            QuickCaptureStatusText.Text = message;
            OcrStatusText.Text = message;
            DiagnosticStatusText.Text = "Scan status: Quick Capture found no anchored payout";
            ShowQuickCaptureStatusNotification(message);
            ShowCompactScanMessage(
                "No payout found\nMake sure the selected area includes both “TOTAL CASH EARNED” and the full amount.");
            return;
        }

        if (_pendingDetectedPayout is not null)
        {
            QuickCaptureStatusText.Text = "A payout is already waiting for confirmation.";
            ShowQuickCaptureStatusNotification("A payout is already waiting for confirmation.");
            return;
        }

        if (!_duplicateGuard.ShouldPresent(candidate, DateTime.UtcNow))
        {
            QuickCaptureStatusText.Text = "Duplicate result ignored.";
            OcrStatusText.Text = "Duplicate on-screen notification ignored.";
            DiagnosticStatusText.Text = "Scan status: Duplicate Quick Capture suppressed";
            ShowQuickCaptureStatusNotification("Duplicate result ignored.");
            return;
        }

        if (!IsTotalCashEarnedDetection(candidate.DetectionMethod))
        {
            PresentPayoutCandidate(candidate, "Quick Capture");
            QuickCaptureStatusText.Text = "A plus-sign payout needs confirmation.";
            ShowQuickCaptureStatusNotification("Payout needs confirmation.");
            return;
        }

        PresentPayoutCandidate(candidate, "Quick Scan");
        SetQuickScanState("Verify detected amount", "QUICK SCAN");
        QuickCaptureStatusText.Text = candidate.IsAmbiguous
            ? "Reader results disagree. Check or edit the amount before saving."
            : "Check the detected amount, then confirm, edit, or reject it.";
        OcrStatusText.Text = "Quick Scan result is waiting for your confirmation.";
        DiagnosticStatusText.Text = "Scan status: Quick Scan awaiting manual verification";
        ShowQuickCaptureStatusNotification("Verify the detected payout before saving.");
        await Task.CompletedTask;
    }

    private async void AutomaticDetectionCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        var automaticCaptureArea = GetAutomaticCaptureArea();
        if (automaticCaptureArea is null)
        {
            AutomaticDetectionCheckBox.IsChecked = false;
            OcrStatusText.Text = "Selected Region mode requires a valid capture area.";
            DiagnosticStatusText.Text = "Scan status: Capture area missing";
            return;
        }

        StopAutomaticDetection();
        _livePreviewTimer.Stop();
        _automaticDetectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _shutdownCancellation.Token);
        UpdateDashboardStatus();
        try
        {
            await _backgroundScannerService.StartAsync(
                automaticCaptureArea,
                _captureSettings.DetectionMode,
                _automaticDetectionCancellation.Token);
            OcrStatusText.Text = _continuousCaptureService.Backend == ScreenCaptureBackend.GdiFallback
                ? $"Automatic detection active. {_continuousCaptureService.FallbackReason}"
                : "Automatic detection active.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            OcrStatusText.Text = $"Automatic detection could not start: {ex.Message}";
            DiagnosticStatusText.Text = "Scan status: Capture startup failed";
            AutomaticDetectionCheckBox.IsChecked = false;
        }
    }

    private CaptureSettings? GetAutomaticCaptureArea()
    {
        return HasConfirmedCaptureArea()
            ? new CaptureSettings
            {
                X = _captureSettings.X,
                Y = _captureSettings.Y,
                Width = _captureSettings.Width,
                Height = _captureSettings.Height
            }
            : null;
    }

    private bool HasConfirmedCaptureArea()
    {
        return _captureSettings.CaptureAreaConfirmed &&
            _screenCaptureService.IsValidCaptureArea(_captureSettings);
    }

    private void AutomaticDetectionCheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        StopAutomaticDetection();
        if (!_isClosing)
        {
            OcrStatusText.Text = "Automatic detection stopped.";
            if (IsVisible && LivePreviewCheckBox.IsChecked == true)
            {
                _livePreviewTimer.Start();
            }
        }

        UpdateDashboardStatus();
    }

    private void StopAutomaticDetection()
    {
        var cancellation = _automaticDetectionCancellation;
        _automaticDetectionCancellation = null;
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        cancellation.Dispose();
        _ = _backgroundScannerService.StopAsync();
    }

    private void BackgroundScannerService_FrameAvailable(object? sender, ScannerFrameEventArgs e)
    {
        if (_isClosing || !IsVisible || LivePreviewCheckBox.IsChecked != true)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_isClosing || !IsVisible || LivePreviewCheckBox.IsChecked != true)
            {
                return;
            }

            CapturePreviewImage.Source = e.Frame;
            CapturePreviewPlaceholderText.Visibility = Visibility.Collapsed;
        }, DispatcherPriority.Background);
    }

    private void BackgroundScannerService_SnapshotChanged(object? sender, ScannerSnapshotEventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_isClosing)
            {
                return;
            }

            var active = AutomaticDetectionCheckBox.IsChecked == true &&
                e.Snapshot.State is not (BackgroundScannerState.Disabled or BackgroundScannerState.Paused or BackgroundScannerState.Error);
            _trayIconService?.UpdateScannerStatus(active, e.Snapshot.State.ToString());
            if (!IsVisible)
            {
                return;
            }

            OcrStatusText.Text = e.Snapshot.Message;
            DiagnosticStatusText.Text = $"Scan status: {e.Snapshot.State}";
            UpdateDashboardStatus();
        }, DispatcherPriority.Background);
    }

    private void BackgroundScannerService_AnalysisCompleted(object? sender, ScannerAnalysisEventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(async () =>
        {
            if (_isClosing)
            {
                return;
            }

            _latestGtaDiagnostics = e.Analysis.GtaResultDiagnostics;
            if (IsVisible)
            {
                UpdateOcrDiagnostics(e.Analysis);
            }

            await HandleBackgroundAnalysisAsync(e.Analysis);
        }, DispatcherPriority.Background);
    }

    private async Task HandleBackgroundAnalysisAsync(PayoutAnalysisResult analysis)
    {
        var candidate = analysis.Candidate;
        if (candidate is null)
        {
            _duplicateGuard.RecordNoCandidate(analysis.ImageFingerprint);
            return;
        }

        if (_pendingDetectedPayout is not null ||
            !_duplicateGuard.ShouldPresent(candidate, DateTime.UtcNow))
        {
            return;
        }

        var decision = QuickCapturePolicy.Evaluate(candidate, requireManualConfirmation: false);
        if (decision == QuickCaptureDecision.AutomaticallyAdd)
        {
            var saved = await SaveEarningAmountAsync(candidate.Amount, "Automatic Detection");
            if (saved is null)
            {
                return;
            }

            _duplicateGuard.RecordDismissal(candidate, DateTime.UtcNow);
            if (IsVisible)
            {
                OcrStatusText.Text = $"Automatically added {FormatMoney(candidate.Amount)}.";
                ShowQuickCaptureNotification(saved);
            }
            else if (_captureSettings.ShowTrayNotificationAfterPayout)
            {
                _trayIconService?.ShowPayoutNotification(candidate.Amount, automaticallyAdded: true);
            }

            return;
        }

        if (decision == QuickCaptureDecision.RequireConfirmation)
        {
            PresentPayoutCandidate(candidate, "Automatic Detection");
            if (!IsVisible && _captureSettings.ShowTrayNotificationAfterPayout)
            {
                _trayIconService?.ShowPayoutNotification(candidate.Amount, automaticallyAdded: false);
            }
        }
    }

    private async Task RunOcrScanAsync(CancellationToken cancellationToken)
    {
        if (!HasConfirmedCaptureArea())
        {
            OcrStatusText.Text = "No capture area selected.";
            DiagnosticStatusText.Text = "Scan status: Capture area missing";
            return;
        }

        bool enteredScan;
        try
        {
            enteredScan = await _ocrScanSemaphore.WaitAsync(0, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!enteredScan)
        {
            OcrStatusText.Text = "An OCR scan is already running.";
            return;
        }

        try
        {
            OcrStatusText.Text = "Analyzing captured pixels locally...";
            DiagnosticStatusText.Text = "Scan status: Scanning";
            AnalyzeCaptureButton.IsEnabled = false;
            var settings = _captureSettings;

            var scanResult = await Task.Run(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var capturedImage = _screenCaptureService.Capture(settings);
                    var analysis = _payoutDetectionService.Analyze(capturedImage, cancellationToken);
                    return (CapturedImage: capturedImage, Analysis: analysis);
                },
                cancellationToken);

            if (_isClosing || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            CapturePreviewImage.Source = scanResult.CapturedImage;
            CapturePreviewPlaceholderText.Visibility = Visibility.Collapsed;
            UpdateOcrDiagnostics(scanResult.Analysis);
            HandlePayoutAnalysis(scanResult.Analysis);
        }
        catch (OperationCanceledException)
        {
            if (!_isClosing)
            {
                OcrStatusText.Text = "OCR scan cancelled.";
                DiagnosticStatusText.Text = "Scan status: Cancelled";
            }
        }
        catch (FileNotFoundException ex)
        {
            if (_isClosing)
            {
                return;
            }

            AutomaticDetectionCheckBox.IsChecked = false;
            OcrStatusText.Text = "OCR data is missing. Rebuild the app and verify tessdata/eng.traineddata.";
            DiagnosticStatusText.Text = $"Scan status: {ex.Message}";
        }
        catch (InvalidOperationException ex)
        {
            if (_isClosing)
            {
                return;
            }

            AutomaticDetectionCheckBox.IsChecked = false;
            OcrStatusText.Text = ex.Message;
            DiagnosticStatusText.Text = "Scan status: OCR initialization or image error";
        }
        catch (Exception)
        {
            if (_isClosing)
            {
                return;
            }

            OcrStatusText.Text = "OCR failed while analyzing the current capture.";
            DiagnosticStatusText.Text = "Scan status: Capture or OCR exception";
        }
        finally
        {
            if (!_isClosing)
            {
                AnalyzeCaptureButton.IsEnabled = true;
                UpdateDashboardStatus();
            }

            _ocrScanSemaphore.Release();
        }
    }

    private void UpdateOcrDiagnostics(PayoutAnalysisResult analysis)
    {
        _latestGtaDiagnostics = analysis.GtaResultDiagnostics;
        DiagnosticRawText.Text = string.IsNullOrWhiteSpace(analysis.RawText)
            ? "(no text detected)"
            : analysis.RawText;
        DiagnosticConfidenceText.Text = analysis.Confidence is float confidence
            ? $"Confidence: {QuickCapturePolicy.NormalizeConfidence(confidence):P0}"
            : "Confidence: --";
        DiagnosticMethodText.Text = $"Preprocessing: {analysis.PreprocessingMethod}";
        DiagnosticDetectionMethodText.Text = analysis.Candidate is null
            ? "Detection method: None"
            : $"Detection method: {GetDetectionMethodName(analysis.Candidate.DetectionMethod)}";
        DiagnosticScanTimeText.Text =
            $"Last scan: {DateTime.Now.ToString("h:mm:ss tt", CultureInfo.CurrentCulture)}";

        var gta = analysis.GtaResultDiagnostics;
        if (gta is null)
        {
            GtaDiagnosticSummaryText.Text = "No specialized scan data.";
            GtaVariantDiagnosticsText.Text = "(none)";
            GtaDiagnosticPreviewPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var anchorBounds = gta.AnchorBounds is Int32Rect bounds
            ? $"X {bounds.X}, Y {bounds.Y}, W {bounds.Width}, H {bounds.Height}"
            : "unavailable";
        GtaDiagnosticSummaryText.Text = string.Join(
            Environment.NewLine,
            $"Anchor found: {(gta.AnchorFound ? "yes" : "no")}",
            $"Anchor OCR: {OneLine(gta.AnchorOcrText)}",
            $"Anchor confidence: {gta.AnchorConfidence:P0}",
            $"Anchor bounds: {anchorBounds}",
            $"Amount crop: X {gta.AmountCropBounds.X}, Y {gta.AmountCropBounds.Y}, W {gta.AmountCropBounds.Width}, H {gta.AmountCropBounds.Height}",
            $"Selected amount: {(gta.SelectedAmount is long amount ? FormatMoney(amount) : "none")}",
            $"Amount confidence: {gta.AmountConfidence:P0}",
            $"Selected variant: {gta.SelectedPreprocessingMethod} / {gta.SelectedPageSegmentationMode}",
            $"Agreeing variants: {gta.AgreeingVariantCount}",
            $"Stable burst frames: {gta.StableFrameCount}",
            $"Independent readers: {gta.IndependentReaderAgreement}",
            $"Competing candidates: {gta.CompetingCandidates}",
            $"Uncertain digit positions: {gta.UncertainDigitPositions}",
            $"Automatic-add decision: {(gta.AutomaticAddAccepted ? "accepted" : "confirmation required")}",
            $"Reason: {gta.AutomaticAddReason}");

        if (_captureSettings.ShowScannerPerformanceDiagnostics && gta.Performance is { } performance)
        {
            GtaDiagnosticSummaryText.Text += Environment.NewLine + string.Join(
                Environment.NewLine,
                "",
                $"Capture backend: {performance.CaptureBackend}",
                $"Window visibility: {(IsVisible ? "Visible" : "Hidden")}",
                $"Scanner active while hidden: {(_captureSettings.ContinueScannerWhileHidden ? "Yes" : "No")}",
                $"Frame change: {performance.FrameChangePercentage:0.00}%",
                $"Frame stability: {performance.FrameStabilityMilliseconds:0} ms",
                $"Frames received / ignored: {performance.FramesReceived} / {performance.FramesIgnoredAsUnchanged}",
                $"OCR scans performed: {performance.OcrScansPerformed}",
                $"Heading preprocessing / OCR: {performance.HeadingPreprocessingMilliseconds:0.0} / {performance.HeadingOcrMilliseconds:0.0} ms",
                $"Amount crop / preprocessing / OCR: {performance.AmountCropMilliseconds:0.0} / {performance.AmountPreprocessingMilliseconds:0.0} / {performance.AmountOcrMilliseconds:0.0} ms",
                $"Fallback: {performance.FallbackMilliseconds:0.0} ms",
                $"Total detection: {performance.TotalScanMilliseconds:0.0} ms",
                $"Path / variants: {performance.DetectionPath} / {performance.PreprocessingVariantsAttempted}",
                $"Rejection reason: {(string.IsNullOrWhiteSpace(performance.RejectionReason) ? "None" : performance.RejectionReason)}",
                $"Last successful detection: {(performance.LastSuccessfulDetectionUtc is DateTime detectedAt ? detectedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "None")}");
        }

        GtaVariantDiagnosticsText.Text = gta.AmountOcrResults.Count == 0
            ? "(no amount variants were run)"
            : string.Join(
                Environment.NewLine,
                gta.AmountOcrResults.Select(result =>
                    $"{result.PreprocessingMethod} / {result.PageSegmentationMode} | {result.Confidence:P0} | " +
                    $"raw: {OneLine(result.RawText)} | normalized: " +
                    $"{(string.IsNullOrWhiteSpace(result.NormalizedText) ? "--" : result.NormalizedText)}"));

        if (gta.DigitOcrResults is { Count: > 0 })
        {
            GtaVariantDiagnosticsText.Text += Environment.NewLine + Environment.NewLine +
                "SEGMENTED DIGITS" + Environment.NewLine +
                string.Join(
                    Environment.NewLine,
                    gta.DigitOcrResults.Select(digit =>
                        $"Position {digit.Position}: {digit.SelectedDigit?.ToString() ?? "?"} | " +
                        $"box {digit.Bounds.X},{digit.Bounds.Y} {digit.Bounds.Width}x{digit.Bounds.Height} | " +
                        $"confidence {digit.Confidence:P0} | holes {digit.EnclosedRegionCount?.ToString() ?? "--"} | " +
                        $"{digit.ShapeSupport}"));
        }

        GtaHeadingCropPreview.Source = gta.HeadingCropPreview;
        GtaAmountCropPreview.Source = gta.AmountCropPreview;
        GtaProcessedAmountPreview.Source = gta.BestProcessedAmountPreview;
        GtaDiagnosticPreviewPanel.Visibility =
            gta.HeadingCropPreview is not null || gta.AmountCropPreview is not null || gta.BestProcessedAmountPreview is not null
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void HandlePayoutAnalysis(PayoutAnalysisResult analysis)
    {
        if (analysis.Candidate is null)
        {
            _duplicateGuard.RecordNoCandidate(analysis.ImageFingerprint);
            if (string.IsNullOrWhiteSpace(analysis.RawText))
            {
                OcrStatusText.Text = "No text detected.";
                DiagnosticStatusText.Text = "Scan status: No text detected";
            }
            else
            {
                OcrStatusText.Text = "Text detected, but no valid positive payout was found.";
                DiagnosticStatusText.Text = "Scan status: No valid payout candidate";
            }

            return;
        }

        if (_pendingDetectedPayout is not null)
        {
            OcrStatusText.Text = "A payout is already waiting for confirmation.";
            DiagnosticStatusText.Text = "Scan status: Candidate pending confirmation";
            return;
        }

        if (!_duplicateGuard.ShouldPresent(analysis.Candidate, DateTime.UtcNow))
        {
            OcrStatusText.Text = "Duplicate on-screen notification ignored.";
            DiagnosticStatusText.Text = "Scan status: Duplicate candidate suppressed";
            return;
        }

        PresentPayoutCandidate(analysis.Candidate, "OCR");
    }

    private void PresentPayoutCandidate(DetectedPayout candidate, string source)
    {
        _pendingDetectedPayout = candidate;
        _pendingPayoutSource = source;
        var isTotalCashEarned = IsTotalCashEarnedDetection(candidate.DetectionMethod);
        ConfirmationHeadingText.Text = isTotalCashEarned
            ? "Total Cash Earned detected"
            : "PAYOUT REQUIRES CONFIRMATION";
        DetectedPayoutText.Text = isTotalCashEarned
            ? $"Amount: {FormatMoney(candidate.Amount)}"
            : $"Detected payout: {FormatMoney(candidate.Amount)}";
        RecognizedPayoutText.Text = $"Recognized text: {candidate.OriginalOcrText}";
        ConfirmationMetaText.Text =
            $"Confidence: {QuickCapturePolicy.NormalizeConfidence(candidate.Confidence):P0}  •  {candidate.PreprocessingMethod}";
        var diagnostics = _latestGtaDiagnostics;
        _pendingPayoutScreenshotPng = IsQuickScanSource(source) &&
            _discordSettings.IncludeQuickScanScreenshot
                ? EncodePng(diagnostics?.AmountCropPreview ?? diagnostics?.BestProcessedAmountPreview)
                : null;
        ConfirmationDiagnosticsText.Text = diagnostics is null
            ? "Independent readers: unavailable"
            : string.Join(
                Environment.NewLine,
                $"Independent readers agreeing: {diagnostics.IndependentReaderAgreement}",
                $"Digit count: {candidate.Amount.ToString(CultureInfo.InvariantCulture).Length}",
                $"Competing candidates: {diagnostics.CompetingCandidates}",
                $"Uncertain digit positions: {diagnostics.UncertainDigitPositions}");
        ConfirmationAmountCropImage.Source = diagnostics?.AmountCropPreview;
        ConfirmationProcessedCropImage.Source = diagnostics?.BestProcessedAmountPreview;
        ConfirmationPreviewPanel.Visibility =
            diagnostics?.AmountCropPreview is not null || diagnostics?.BestProcessedAmountPreview is not null
                ? Visibility.Visible
                : Visibility.Collapsed;
        PayoutConfirmationCard.Visibility = Visibility.Visible;
        CompactPendingIndicator.Visibility = Visibility.Visible;
        CompactNormalPanel.Visibility = Visibility.Collapsed;
        CompactEditPanel.Visibility = Visibility.Collapsed;
        CompactPendingText.Text = FormatMoney(candidate.Amount);
        CompactPendingMetaText.Text =
            $"Confidence: {QuickCapturePolicy.NormalizeConfidence(candidate.Confidence):P0}  •  Readers agreeing: {diagnostics?.IndependentReaderAgreement ?? 0}";
        CompactPendingConflictText.Text = diagnostics is null || diagnostics.CompetingCandidates == "None"
            ? "Possible conflict: None"
            : $"Alternative: {diagnostics.CompetingCandidates}  •  Uncertain digit: {diagnostics.UncertainDigitPositions}";
        CompactPendingPreviewImage.Source = diagnostics?.AmountCropPreview;
        ExpandedEditPanel.Visibility = Visibility.Collapsed;
        OcrStatusText.Text = "Payout candidate detected. Confirm, reject, or edit it.";
        DiagnosticStatusText.Text = "Scan status: Candidate awaiting manual confirmation";
        _backgroundScannerService.SetPaused(paused: true);
        if (_isCompactMode)
        {
            AnimateWindowSize(CompactWindowWidth, CompactPendingHeight);
        }
        AnimateConfirmationCard();
        if (!_isCompactMode && _selectedTab.Equals("Dashboard", StringComparison.Ordinal))
        {
            PayoutConfirmationCard.BringIntoView();
        }
    }

    private async void ConfirmPayoutButton_Click(object sender, RoutedEventArgs e)
    {
        var pendingPayout = _pendingDetectedPayout;
        if (pendingPayout is null)
        {
            return;
        }

        PayoutConfirmationCard.IsEnabled = false;
        try
        {
            var source = _pendingPayoutSource;
            var screenshotPng = _pendingPayoutScreenshotPng;
            var savedRecord = await SaveEarningAmountAsync(pendingPayout.Amount, source);
            if (savedRecord is null)
            {
                OcrStatusText.Text = "The detected payout could not be saved.";
                return;
            }

            _duplicateGuard.RecordDismissal(pendingPayout, DateTime.UtcNow);
            ClearPendingPayout();
            OcrStatusText.Text = "Payout confirmed and saved.";
            DiagnosticStatusText.Text = "Scan status: Candidate confirmed";
            ShowCompactScanMessage($"Added {FormatMoney(pendingPayout.Amount)}");
            await SendConfirmedPayoutToDiscordAsync(savedRecord, source, screenshotPng);
        }
        finally
        {
            PayoutConfirmationCard.IsEnabled = true;
        }
    }

    private void RejectPayoutButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingDetectedPayout is null)
        {
            return;
        }

        _duplicateGuard.RecordDismissal(_pendingDetectedPayout, DateTime.UtcNow);
        ClearPendingPayout();
        OcrStatusText.Text = "Detected payout rejected.";
        DiagnosticStatusText.Text = "Scan status: Candidate rejected";
    }

    private void EditDetectedAmountButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingDetectedPayout is null)
        {
            return;
        }

        var text = _pendingDetectedPayout.Amount.ToString("#,##0", MoneyCulture);
        if (_isCompactMode)
        {
            CompactPendingIndicator.Visibility = Visibility.Collapsed;
            CompactEditPanel.Visibility = Visibility.Visible;
            CompactEditAmountTextBox.Text = text;
            CompactEditValidationText.Text = string.Empty;
            CompactEditAmountTextBox.Focus();
            CompactEditAmountTextBox.SelectAll();
        }
        else
        {
            ExpandedEditPanel.Visibility = Visibility.Visible;
            ExpandedEditAmountTextBox.Text = text;
            ExpandedEditValidationText.Text = string.Empty;
            ExpandedEditAmountTextBox.Focus();
            ExpandedEditAmountTextBox.SelectAll();
        }

        OcrStatusText.Text = "Edit the detected amount, then save or cancel.";
        DiagnosticStatusText.Text = "Scan status: Editing pending candidate";
    }

    private async void SaveEditedPayoutButton_Click(object sender, RoutedEventArgs e)
    {
        var pending = _pendingDetectedPayout;
        if (pending is null)
        {
            return;
        }

        var text = _isCompactMode ? CompactEditAmountTextBox.Text : ExpandedEditAmountTextBox.Text;
        if (!decimal.TryParse(text, AcceptedAmountStyles, MoneyCulture, out var amount) ||
            amount <= 0 || amount > PayoutDetectionService.MaximumPayout)
        {
            var message = "Enter a positive payout no greater than $2,500,000,000.";
            CompactEditValidationText.Text = message;
            ExpandedEditValidationText.Text = message;
            return;
        }

        var source = $"{_pendingPayoutSource} (Edited)";
        var screenshotPng = _pendingPayoutScreenshotPng;
        var savedRecord = await SaveEarningAmountAsync(amount, source);
        if (savedRecord is null)
        {
            return;
        }

        _duplicateGuard.RecordDismissal(pending, DateTime.UtcNow);
        ClearPendingPayout();
        OcrStatusText.Text = "Edited payout saved.";
        ShowCompactScanMessage($"Added {FormatMoney(amount)}");
        await SendConfirmedPayoutToDiscordAsync(savedRecord, source, screenshotPng);
    }

    private void CancelEditedPayoutButton_Click(object sender, RoutedEventArgs e)
    {
        ExpandedEditPanel.Visibility = Visibility.Collapsed;
        CompactEditPanel.Visibility = Visibility.Collapsed;
        if (_pendingDetectedPayout is not null)
        {
            CompactPendingIndicator.Visibility = _isCompactMode ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void PendingEditTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            SaveEditedPayoutButton_Click(sender, e);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelEditedPayoutButton_Click(sender, e);
        }
    }

    private void ClearPendingPayout()
    {
        _pendingDetectedPayout = null;
        _pendingPayoutSource = "OCR";
        _pendingPayoutScreenshotPng = null;
        PayoutConfirmationCard.Visibility = Visibility.Collapsed;
        CompactPendingIndicator.Visibility = Visibility.Collapsed;
        CompactEditPanel.Visibility = Visibility.Collapsed;
        ExpandedEditPanel.Visibility = Visibility.Collapsed;
        CompactNormalPanel.Visibility = Visibility.Visible;
        if (_isCompactMode)
        {
            AnimateWindowSize(CompactWindowWidth, CompactNormalHeight);
        }
        if (AutomaticDetectionCheckBox.IsChecked == true &&
            (IsVisible || _captureSettings.ContinueScannerWhileHidden))
        {
            _backgroundScannerService.SetPaused(paused: false);
        }
    }

    private async Task AddEarningAsync()
    {
        ValidationText.Text = string.Empty;

        if (!decimal.TryParse(
                AmountTextBox.Text,
                AcceptedAmountStyles,
                MoneyCulture,
                out var amount) || amount <= 0)
        {
            ValidationText.Text = "Enter a positive amount, such as 500000, 500,000, or $500,000.";
            return;
        }

        if (await SaveEarningAmountAsync(amount, "Manual") is not null)
        {
            AmountTextBox.Clear();
        }

        AmountTextBox.Focus();
    }

    private async Task<EarningRecord?> SaveEarningAmountAsync(decimal amount, string source)
    {
        var record = new EarningRecord
        {
            Amount = amount,
            TimestampUtc = DateTime.UtcNow,
            Source = source
        };

        _earnings.Add(record);
        SetControlsEnabled(false);

        try
        {
            await _storageService.SaveAsync(_earnings);
            RefreshTotals();
            return record;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _earnings.Remove(record);
            ValidationText.Text = "The earning could not be saved. Check access to the local data folder and try again.";
            return null;
        }
        finally
        {
            SetControlsEnabled(true);
        }
    }

    private async Task SendConfirmedPayoutToDiscordAsync(
        EarningRecord savedRecord,
        string source,
        byte[]? screenshotPng)
    {
        if (!_discordSettings.Enabled ||
            !IsQuickScanSource(source) ||
            string.IsNullOrWhiteSpace(_discordSettings.Username) ||
            !DiscordWebhookService.IsValidWebhookUrl(_discordWebhookUrl))
        {
            return;
        }

        var nowUtc = DateTime.UtcNow;
        var last24Hours = _earnings
            .Where(record => record.TimestampUtc >= nowUtc.AddHours(-24) &&
                record.TimestampUtc <= nowUtc)
            .Sum(record => record.Amount);
        var last7Days = _earnings
            .Where(record => record.TimestampUtc >= nowUtc.AddDays(-7) &&
                record.TimestampUtc <= nowUtc)
            .Sum(record => record.Amount);
        var notification = new DiscordPayoutNotification(
            _discordSettings.Username,
            savedRecord.Amount,
            last24Hours,
            last7Days,
            "Quick Scan",
            savedRecord.TimestampUtc,
            _discordSettings.IncludeQuickScanScreenshot ? screenshotPng : null);

        var result = await _discordWebhookService.SendPayoutAsync(
            _discordWebhookUrl,
            notification,
            _shutdownCancellation.Token);
        SetDiscordStatus(result.Message, result.Success);

        if (result.Success)
        {
            OcrStatusText.Text = "Payout saved locally and sent to Discord.";
            ShowCompactScanMessage($"Added {FormatMoney(savedRecord.Amount)} • Sent to Discord");
        }
        else
        {
            OcrStatusText.Text = $"Payout saved locally. Discord delivery failed: {result.Message}";
            ShowCompactScanMessage($"Added {FormatMoney(savedRecord.Amount)} • Discord failed");
        }
    }

    private static bool IsQuickScanSource(string source) =>
        source.Contains("Quick", StringComparison.OrdinalIgnoreCase);

    private static byte[]? EncodePng(BitmapSource? bitmapSource)
    {
        if (bitmapSource is null)
        {
            return null;
        }

        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private async void UndoLastButton_Click(object sender, RoutedEventArgs e)
    {
        if (_earnings.Count == 0)
        {
            return;
        }

        ValidationText.Text = string.Empty;
        var latestRecord = _earnings.MaxBy(record => record.TimestampUtc)!;
        if (await UndoEarningRecordAsync(
                latestRecord,
                "The last earning could not be undone because the data file could not be saved.") &&
            ReferenceEquals(latestRecord, _quickCaptureNotificationRecord))
        {
            HideQuickCaptureNotification();
        }
    }

    private async Task<bool> UndoEarningRecordAsync(EarningRecord record, string failureMessage)
    {
        var originalIndex = _earnings.IndexOf(record);
        if (originalIndex < 0)
        {
            return false;
        }

        _earnings.RemoveAt(originalIndex);
        SetControlsEnabled(false);

        try
        {
            await _storageService.SaveAsync(_earnings);
            RefreshTotals();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _earnings.Insert(originalIndex, record);
            ValidationText.Text = failureMessage;
            return false;
        }
        finally
        {
            SetControlsEnabled(true);
            RefreshTotals();
        }
    }

    private void SetQuickCaptureProcessing(bool isProcessing)
    {
        var canScan = !isProcessing && HasConfirmedCaptureArea();
        QuickCaptureButton.IsEnabled = canScan;
        CompactQuickCaptureButton.IsEnabled = canScan;
        if (!isProcessing)
        {
            QuickCaptureButton.Content = "QUICK SCAN";
            CompactQuickCaptureButton.Content = "QUICK SCAN";
        }
    }

    private void SetQuickScanState(string state, string buttonText)
    {
        QuickCaptureStatusText.Text = state;
        QuickCaptureButton.Content = buttonText;
        CompactQuickCaptureButton.Content = buttonText;
        CompactScannerStatusText.Text = state;
    }

    private void ShowCompactScanMessage(string message)
    {
        CompactScanMessageText.Text = message
            .Replace("\r\n", " — ", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Replace('\r', ' ');
        CompactScanMessageText.ToolTip = message;
        CompactStatusPanel.Visibility = Visibility.Collapsed;
        CompactScanMessageBorder.Visibility = Visibility.Visible;
        CompactScanMessageBorder.Opacity = 0;
        if (CompactScanMessageBorder.RenderTransform is TranslateTransform transform)
        {
            transform.Y = -5;
            transform.BeginAnimation(
                TranslateTransform.YProperty,
                new DoubleAnimation(-5, 0, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                });
        }

        CompactScanMessageBorder.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        _quickCaptureNotificationTimer.Stop();
        _quickCaptureNotificationTimer.Start();
    }

    private void HideCompactScanMessage()
    {
        if (CompactScanMessageBorder.Visibility != Visibility.Visible)
        {
            CompactStatusPanel.Visibility = Visibility.Visible;
            return;
        }

        var fade = new DoubleAnimation(CompactScanMessageBorder.Opacity, 0, TimeSpan.FromMilliseconds(120));
        fade.Completed += (_, _) =>
        {
            CompactScanMessageBorder.Visibility = Visibility.Collapsed;
            CompactScanMessageBorder.Opacity = 1;
            CompactStatusPanel.Visibility = Visibility.Visible;
        };
        CompactScanMessageBorder.BeginAnimation(OpacityProperty, fade);
    }

    private void ShowQuickCaptureNotification(EarningRecord savedRecord)
    {
        _quickCaptureNotificationRecord = savedRecord;
        if (_isCompactMode)
        {
            QuickCaptureNotification.Visibility = Visibility.Collapsed;
            ShowCompactScanMessage($"✓ Added {FormatMoney(savedRecord.Amount)}");
            return;
        }

        QuickCaptureNotificationAmount.Text = $"Added {FormatMoney(savedRecord.Amount)}";
        QuickCaptureNotificationAmount.Foreground = GetThemeBrush("EarningsGreenBrush");
        QuickCaptureNotificationUndoButton.Visibility = Visibility.Visible;
        QuickCaptureNotification.Visibility = Visibility.Visible;
        _quickCaptureNotificationTimer.Stop();
        _quickCaptureNotificationTimer.Start();
    }

    private void ShowQuickCaptureStatusNotification(string message)
    {
        _quickCaptureNotificationRecord = null;
        if (_isCompactMode)
        {
            QuickCaptureNotification.Visibility = Visibility.Collapsed;
            ShowCompactScanMessage(message);
            return;
        }

        QuickCaptureNotificationAmount.Text = message;
        QuickCaptureNotificationAmount.Foreground = GetThemeBrush("PrimaryTextBrush");
        QuickCaptureNotificationUndoButton.Visibility = Visibility.Collapsed;
        QuickCaptureNotification.Visibility = Visibility.Visible;
        _quickCaptureNotificationTimer.Stop();
        _quickCaptureNotificationTimer.Start();
    }

    private void HideQuickCaptureNotification()
    {
        _quickCaptureNotificationTimer.Stop();
        QuickCaptureNotification.Visibility = Visibility.Collapsed;
        _quickCaptureNotificationRecord = null;
    }

    private async void QuickCaptureUndoButton_Click(object sender, RoutedEventArgs e)
    {
        var record = _quickCaptureNotificationRecord;
        if (record is null)
        {
            HideQuickCaptureNotification();
            return;
        }

        if (await UndoEarningRecordAsync(
                record,
                "The Quick Capture earning could not be undone because the data file could not be saved."))
        {
            QuickCaptureStatusText.Text = "Quick Capture earning undone.";
            HideQuickCaptureNotification();
        }
    }

    private void RefreshTotals()
    {
        var nowUtc = DateTime.UtcNow;
        var dayCutoffUtc = nowUtc.AddHours(-24);
        var weekCutoffUtc = nowUtc.AddDays(-7);
        var latestRecord = _earnings.MaxBy(record => record.TimestampUtc);
        var lastPayout = latestRecord?.Amount ?? 0;
        var dayTotal = _earnings
            .Where(record => record.TimestampUtc >= dayCutoffUtc && record.TimestampUtc <= nowUtc)
            .Sum(record => record.Amount);
        var weekTotal = _earnings
            .Where(record => record.TimestampUtc >= weekCutoffUtc && record.TimestampUtc <= nowUtc)
            .Sum(record => record.Amount);

        LastPayoutText.Text = FormatMoney(lastPayout);
        HeroLastPayoutText.Text = FormatMoney(lastPayout);
        DayTotalText.Text = FormatMoney(dayTotal);
        WeekTotalText.Text = FormatMoney(weekTotal);
        CompactDayTotalText.Text = FormatMoney(dayTotal);
        CompactWeekTotalText.Text = FormatMoney(weekTotal);
        CompactLastPayoutText.Text = FormatMoney(lastPayout);
        LastPayoutTimestampText.Text = latestRecord is null
            ? "No transactions yet"
            : FormatLocalTimestamp(latestRecord.TimestampUtc);
        LastUpdatedText.Text = $"Last updated: {DateTime.Now.ToString("h:mm tt", CultureInfo.CurrentCulture)}";
        UndoLastButton.IsEnabled = _earnings.Count > 0 && AddEarningButton.IsEnabled;
        RecentUndoButton.IsEnabled = _earnings.Count > 0 && AddEarningButton.IsEnabled;
        RefreshWeeklyChart();
        RefreshRecentActivity();
    }

    private void UpdateAmountPlaceholder()
    {
        AmountPlaceholderText.Visibility = string.IsNullOrEmpty(AmountTextBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SetControlsEnabled(bool isEnabled)
    {
        AddEarningButton.IsEnabled = isEnabled;
        AmountTextBox.IsEnabled = isEnabled;
        UndoLastButton.IsEnabled = isEnabled && _earnings.Count > 0;
        RecentUndoButton.IsEnabled = isEnabled && _earnings.Count > 0;
    }

    private void RefreshWeeklyChart()
    {
        var firstDay = DateTime.Today.AddDays(-6);
        var dailyTotals = Enumerable.Range(0, 7)
            .Select(offset =>
            {
                var day = firstDay.AddDays(offset);
                var total = _earnings
                    .Where(record => record.TimestampUtc.ToLocalTime().Date == day)
                    .Sum(record => record.Amount);
                return (Day: day, Total: total);
            })
            .ToList();
        var largestTotal = dailyTotals.Max(item => item.Total);

        WeeklyChartItems.ItemsSource = dailyTotals.Select((item, index) =>
        {
            var barHeight = largestTotal <= 0
                ? 3
                : item.Total <= 0
                    ? 3
                    : Math.Max(7, (double)(item.Total / largestTotal) * 78);
            return new DailyEarningsChartItem(
                item.Day.ToString("ddd", CultureInfo.CurrentCulture)[..1],
                FormatMoney(item.Total),
                barHeight,
                item.Total <= 0 ? 0.18 : 0.62 + (index * 0.055));
        }).ToList();
    }

    private void RefreshRecentActivity()
    {
        var recentItems = _earnings
            .OrderByDescending(record => record.TimestampUtc)
            .Take(5)
            .Select(record => new RecentEarningItem(
                string.IsNullOrWhiteSpace(record.Source) ? "Earning" : record.Source,
                FormatMoney(record.Amount),
                FormatLocalTimestamp(record.TimestampUtc)))
            .ToList();

        RecentActivityItems.ItemsSource = recentItems;
        NoRecentActivityText.Visibility = recentItems.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateDashboardStatus()
    {
        var hasCaptureArea = GetAutomaticCaptureArea() is not null;
        var ocrReady = _ocrService.IsInitialized;
        var quickScanStatus = _pendingDetectedPayout is not null
            ? "Verify payout"
            : hasCaptureArea
                ? "Quick Scan ready"
                : "Select payout area";
        const bool automaticDetectionEnabled = false;

        LiveScannerStatusText.Text = quickScanStatus;
        LiveScannerDot.Fill = GetThemeBrush(hasCaptureArea ? "EarningsGreenBrush" : "WarningGoldBrush");
        CompactScannerStatusText.Text = quickScanStatus;
        CompactLiveScannerDot.Fill = GetThemeBrush(hasCaptureArea ? "EarningsGreenBrush" : "WarningGoldBrush");

        OcrReadyStatusText.Text = ocrReady ? "Ready" : "Standby";
        OcrReadyDot.Fill = GetThemeBrush(ocrReady ? "ScannerCyanBrush" : "MutedTextBrush");

        CaptureAreaStatusText.Text = hasCaptureArea ? "Area set" : "Not set";
        CaptureAreaDot.Fill = GetThemeBrush(hasCaptureArea ? "EarningsGreenBrush" : "WarningGoldBrush");
        CompactCaptureReadyDot.Fill = GetThemeBrush(hasCaptureArea ? "EarningsGreenBrush" : "WarningGoldBrush");
        CompactCaptureReadyText.Text = hasCaptureArea ? "AREA READY" : "AREA REQUIRED";
        ScannerCollapsedStatusText.Text =
            $"{(automaticDetectionEnabled ? "Automatic detection active" : "Scanner idle")}  •  " +
            (hasCaptureArea ? "Capture area ready" : "No capture area");

        SetScannerPulse(false);
    }

    private void SetScannerPulse(bool isActive)
    {
        if (isActive == _scannerPulseRunning)
        {
            return;
        }

        _scannerPulseRunning = isActive;
        if (!isActive)
        {
            LiveScannerDot.BeginAnimation(OpacityProperty, null);
            CompactLiveScannerDot.BeginAnimation(OpacityProperty, null);
            LiveScannerDot.Opacity = 1;
            CompactLiveScannerDot.Opacity = 1;
            return;
        }

        var pulse = new DoubleAnimation
        {
            From = 0.38,
            To = 1,
            Duration = TimeSpan.FromSeconds(1.15),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        LiveScannerDot.BeginAnimation(OpacityProperty, pulse);
        CompactLiveScannerDot.BeginAnimation(OpacityProperty, pulse);
    }

    private void AnimateConfirmationCard()
    {
        PayoutConfirmationCard.Opacity = 0;
        var transform = PayoutConfirmationCard.RenderTransform as TranslateTransform;
        if (transform is not null)
        {
            transform.Y = 10;
            transform.BeginAnimation(
                TranslateTransform.YProperty,
                new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(190))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                });
        }

        PayoutConfirmationCard.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    private void SelectTab(string tabName, bool saveSelection)
    {
        _selectedTab = tabName.Equals("Settings", StringComparison.OrdinalIgnoreCase)
            ? "Settings"
            : "Dashboard";
        MainScrollViewer.Visibility = _selectedTab == "Dashboard"
            ? Visibility.Visible
            : Visibility.Collapsed;
        SettingsScrollViewer.Visibility = _selectedTab == "Settings"
            ? Visibility.Visible
            : Visibility.Collapsed;
        DashboardTabButton.Style = (Style)FindResource(
            _selectedTab == "Dashboard" ? "NavigationTabButtonActive" : "NavigationTabButton");
        SettingsTabButton.Style = (Style)FindResource(
            _selectedTab == "Settings" ? "NavigationTabButtonActive" : "NavigationTabButton");

        if (saveSelection)
        {
            _captureSettings.LastSelectedTab = _selectedTab;
        }
    }

    private async Task SelectTabAnimatedAsync(string tabName)
    {
        var targetTab = tabName.Equals("Settings", StringComparison.OrdinalIgnoreCase)
            ? "Settings"
            : "Dashboard";
        if (_isTabTransitioning || string.Equals(_selectedTab, targetTab, StringComparison.Ordinal))
        {
            return;
        }

        _isTabTransitioning = true;
        var movingToSettings = targetTab == "Settings";
        var outgoing = movingToSettings ? MainScrollViewer : SettingsScrollViewer;
        var incoming = movingToSettings ? SettingsScrollViewer : MainScrollViewer;
        var direction = movingToSettings ? 1d : -1d;
        var outgoingTransform = (TranslateTransform)outgoing.RenderTransform;
        var incomingTransform = (TranslateTransform)incoming.RenderTransform;

        incoming.Visibility = Visibility.Visible;
        incoming.Opacity = 0;
        incomingTransform.X = 42 * direction;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(240);
        var incomingOpacity = new DoubleAnimation(0, 1, duration) { EasingFunction = easing };
        incomingOpacity.Completed += (_, _) => completion.TrySetResult();
        outgoing.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(170)) { EasingFunction = easing });
        outgoingTransform.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(0, -28 * direction, TimeSpan.FromMilliseconds(190)) { EasingFunction = easing });
        incoming.BeginAnimation(OpacityProperty, incomingOpacity);
        incomingTransform.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(42 * direction, 0, duration) { EasingFunction = easing });
        await completion.Task;

        outgoing.Visibility = Visibility.Collapsed;
        outgoing.BeginAnimation(OpacityProperty, null);
        incoming.BeginAnimation(OpacityProperty, null);
        outgoingTransform.BeginAnimation(TranslateTransform.XProperty, null);
        incomingTransform.BeginAnimation(TranslateTransform.XProperty, null);
        outgoing.Opacity = 1;
        incoming.Opacity = 1;
        outgoingTransform.X = 0;
        incomingTransform.X = 0;
        _selectedTab = targetTab;
        _captureSettings.LastSelectedTab = targetTab;
        DashboardTabButton.Style = (Style)FindResource(
            targetTab == "Dashboard" ? "NavigationTabButtonActive" : "NavigationTabButton");
        SettingsTabButton.Style = (Style)FindResource(
            targetTab == "Settings" ? "NavigationTabButtonActive" : "NavigationTabButton");
        incoming.ScrollToTop();
        _isTabTransitioning = false;
    }

    private System.Windows.Controls.ScrollViewer GetSelectedScrollViewer()
    {
        return _selectedTab == "Settings" ? SettingsScrollViewer : MainScrollViewer;
    }

    private void LoadSettingsControls()
    {
        _suppressSettingsUiEvents = true;
        try
        {
            HotkeyEnabledCheckBox.IsChecked = _captureSettings.HotkeyEnabled;
            CurrentHotkeyText.Text = string.IsNullOrWhiteSpace(_captureSettings.HotkeyDisplayText)
                ? "Ctrl + Shift + H"
                : _captureSettings.HotkeyDisplayText;
            _proposedHotkeyModifiers = (GlobalHotkeyModifiers)_captureSettings.HotkeyModifiers;
            _proposedHotkeyVirtualKey = (uint)_captureSettings.HotkeyVirtualKey;
            _proposedHotkeyText = CurrentHotkeyText.Text;
            ProposedHotkeyText.Text = string.Empty;
            AlwaysOnTopSettingCheckBox.IsChecked = _captureSettings.AlwaysOnTop;
            RememberWindowPositionCheckBox.IsChecked = _captureSettings.RememberWindowPosition;
            StartCompactSettingCheckBox.IsChecked = _captureSettings.IsCompactMode;
            PauseLivePreviewWhenHiddenCheckBox.IsChecked = _captureSettings.PauseLivePreviewWhenHidden;
            ContinueScannerWhileHiddenCheckBox.IsChecked = _captureSettings.ContinueScannerWhileHidden;
            ShowTrayNotificationCheckBox.IsChecked = _captureSettings.ShowTrayNotificationAfterPayout;
            ShowScannerPerformanceDiagnosticsCheckBox.IsChecked = _captureSettings.ShowScannerPerformanceDiagnostics;
            ScannerDetectionModeComboBox.SelectedIndex = (int)_captureSettings.DetectionMode;
            ScannerAreaModeComboBox.SelectedIndex = (int)ScannerAreaMode.SelectedRegion;
            RequireQuickConfirmationCheckBox.IsChecked = true;
            ThemeComboBox.SelectedItem = ThemeComboBox.Items
                .OfType<System.Windows.Controls.ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag?.ToString(),
                    _uiPreferences.Theme,
                    StringComparison.OrdinalIgnoreCase)) ?? ThemeComboBox.Items[0];
            DiscordUsernameTextBox.Text = _discordSettings.Username;
            DiscordWebhookPasswordBox.Password = _discordWebhookUrl;
            DiscordEnabledCheckBox.IsChecked = _discordSettings.Enabled;
            DiscordIncludeScreenshotCheckBox.IsChecked =
                _discordSettings.IncludeQuickScanScreenshot;
            SetDiscordStatus(
                _discordSettingsLoadWarning ??
                (_discordSettings.Enabled
                    ? "Discord is enabled for confirmed Quick Scan payouts."
                    : string.IsNullOrWhiteSpace(_discordWebhookUrl)
                        ? "Add a username and webhook URL, then save."
                        : "Discord settings are saved. Sending is currently off."),
                _discordSettingsLoadWarning is null ? null : false);
        }
        finally
        {
            _suppressSettingsUiEvents = false;
        }
    }

    private void PopulateMonitorChoices()
    {
        QuickCaptureMonitorComboBox.Items.Clear();
        QuickCaptureMonitorComboBox.Items.Add(new System.Windows.Controls.ComboBoxItem
        {
            Tag = "Primary",
            Content = "Primary display"
        });
        foreach (var target in _screenCaptureService.GetMonitorTargets())
        {
            QuickCaptureMonitorComboBox.Items.Add(new System.Windows.Controls.ComboBoxItem
            {
                Tag = target.Id,
                Content = target.DisplayName
            });
        }

        QuickCaptureMonitorComboBox.Items.Add(new System.Windows.Controls.ComboBoxItem
        {
            Tag = "All",
            Content = "All connected monitors"
        });
        var selected = QuickCaptureMonitorComboBox.Items
            .OfType<System.Windows.Controls.ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(
                item.Tag as string,
                _captureSettings.QuickCaptureMonitorId,
                StringComparison.OrdinalIgnoreCase));
        QuickCaptureMonitorComboBox.SelectedItem = selected ?? QuickCaptureMonitorComboBox.Items[0];
    }

    private string GetSelectedMonitorDisplayName()
    {
        if (string.Equals(_captureSettings.QuickCaptureMonitorId, "All", StringComparison.OrdinalIgnoreCase))
        {
            return "all connected monitors";
        }

        if (string.Equals(_captureSettings.QuickCaptureMonitorId, "Primary", StringComparison.OrdinalIgnoreCase))
        {
            return "the primary display";
        }

        return _screenCaptureService.GetMonitorTargets()
            .FirstOrDefault(target => string.Equals(
                target.Id,
                _captureSettings.QuickCaptureMonitorId,
                StringComparison.OrdinalIgnoreCase))?.DisplayName ?? "the primary display";
    }

    private void RegisterConfiguredHotkey()
    {
        if (_globalHotkeyService is null)
        {
            HotkeyRegistrationStatusText.Text = "Registration status: Window handle unavailable";
            return;
        }

        if (!_captureSettings.HotkeyEnabled)
        {
            _globalHotkeyService.Disable();
            HotkeyRegistrationStatusText.Text = "Registration status: Disabled";
            return;
        }

        if (_globalHotkeyService.TryRegister(
                (GlobalHotkeyModifiers)_captureSettings.HotkeyModifiers,
                (uint)_captureSettings.HotkeyVirtualKey,
                out _))
        {
            HotkeyRegistrationStatusText.Text =
                $"Registration status: Active ({_captureSettings.HotkeyDisplayText})";
        }
        else
        {
            HotkeyRegistrationStatusText.Text =
                "That shortcut is already being used by another application.";
        }
    }

    private void GlobalHotkeyService_HotkeyPressed(object? sender, EventArgs e)
    {
        if (_isSelectingCaptureArea || _isClosing)
        {
            return;
        }

        if (IsVisible)
        {
            HideMainWindowForScanner();
            return;
        }

        ShowExistingMainWindow();
    }

    private void HideMainWindowForScanner()
    {
        _resumeLivePreviewAfterShow =
            _captureSettings.PauseLivePreviewWhenHidden &&
            LivePreviewCheckBox.IsChecked == true;
        if (_resumeLivePreviewAfterShow)
        {
            _livePreviewTimer.Stop();
        }

        _resumeScannerAfterShow =
            AutomaticDetectionCheckBox.IsChecked == true &&
            !_captureSettings.ContinueScannerWhileHidden;
        if (_resumeScannerAfterShow)
        {
            _backgroundScannerService.SetPaused(paused: true);
        }

        lock (_captureExclusionLock)
        {
            _windowVisibleForCapture = false;
        }

        Hide();
        _trayIconService?.UpdateScannerStatus(
            AutomaticDetectionCheckBox.IsChecked == true && !_resumeScannerAfterShow,
            _resumeScannerAfterShow ? "Paused" : _backgroundScannerService.Snapshot.State.ToString());
    }

    private void ShowExistingMainWindow()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Topmost = _captureSettings.AlwaysOnTop;
        Activate();
        lock (_captureExclusionLock)
        {
            _windowVisibleForCapture = true;
        }
        UpdateWindowCaptureBounds();
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            SetForegroundWindow(handle);
        }

        if (_resumeLivePreviewAfterShow &&
            LivePreviewCheckBox.IsChecked == true &&
            HasConfirmedCaptureArea())
        {
            _resumeLivePreviewAfterShow = false;
            _livePreviewTimer.Start();
        }

        if (_resumeScannerAfterShow)
        {
            _resumeScannerAfterShow = false;
            _backgroundScannerService.SetPaused(paused: false);
        }
    }

    private void TrayIconService_ShowRequested(object? sender, EventArgs e)
    {
        _ = Dispatcher.BeginInvoke(ShowExistingMainWindow);
    }

    private void TrayIconService_ExitRequested(object? sender, EventArgs e)
    {
        _ = Dispatcher.BeginInvoke(Close);
    }

    private void RecordHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _isRecordingHotkey = true;
        ProposedHotkeyText.Text = "Press your new shortcut…";
        HotkeyWarningText.Visibility = Visibility.Collapsed;
        HotkeyRegistrationStatusText.Text = "Press your new shortcut…";
        RecordHotkeyButton.Focus();
    }

    private void RecordProposedHotkey(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            _isRecordingHotkey = false;
            ProposedHotkeyText.Text = string.Empty;
            HotkeyRegistrationStatusText.Text = "Shortcut recording cancelled.";
            return;
        }

        if (IsModifierKey(key))
        {
            ProposedHotkeyText.Text = "Press a non-modifier key…";
            return;
        }

        if (key is Key.Enter or Key.Tab or Key.F12 || key == Key.None)
        {
            HotkeyRegistrationStatusText.Text = "That key cannot be used as the global shortcut.";
            return;
        }

        var modifiers = ConvertModifiers(Keyboard.Modifiers);
        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0)
        {
            HotkeyRegistrationStatusText.Text = "That key could not be converted to a Windows shortcut.";
            return;
        }

        _proposedHotkeyModifiers = modifiers;
        _proposedHotkeyVirtualKey = virtualKey;
        _proposedHotkeyText = FormatHotkey(modifiers, key);
        _isRecordingHotkey = false;
        ProposedHotkeyText.Text = $"Proposed: {_proposedHotkeyText}";
        HotkeyRegistrationStatusText.Text = "Shortcut recorded. Select Apply Shortcut to register it.";
        ShowSingleKeyWarning(modifiers == GlobalHotkeyModifiers.None);
    }

    private void ResetHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _isRecordingHotkey = false;
        _proposedHotkeyModifiers = GlobalHotkeyModifiers.Control | GlobalHotkeyModifiers.Shift;
        _proposedHotkeyVirtualKey = 0x48;
        _proposedHotkeyText = "Ctrl + Shift + H";
        ProposedHotkeyText.Text = "Proposed: Ctrl + Shift + H";
        HotkeyRegistrationStatusText.Text = "Default shortcut selected. Select Apply Shortcut to register it.";
        ShowSingleKeyWarning(show: false);
    }

    private async void ApplyHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_globalHotkeyService is null)
        {
            HotkeyRegistrationStatusText.Text = "The window is not ready to register a shortcut.";
            return;
        }

        var shouldEnable = HotkeyEnabledCheckBox.IsChecked == true;
        if (!shouldEnable)
        {
            _globalHotkeyService.Disable();
            _captureSettings.HotkeyEnabled = false;
            HotkeyRegistrationStatusText.Text = "Registration status: Disabled";
            await SaveSettingsQuietlyAsync();
            return;
        }

        if (!_globalHotkeyService.TryRegister(
                _proposedHotkeyModifiers,
                _proposedHotkeyVirtualKey,
                out _))
        {
            HotkeyRegistrationStatusText.Text =
                "That shortcut is already being used by another application.";
            HotkeyEnabledCheckBox.IsChecked = _captureSettings.HotkeyEnabled;
            _proposedHotkeyModifiers = (GlobalHotkeyModifiers)_captureSettings.HotkeyModifiers;
            _proposedHotkeyVirtualKey = (uint)_captureSettings.HotkeyVirtualKey;
            _proposedHotkeyText = _captureSettings.HotkeyDisplayText;
            ProposedHotkeyText.Text = string.Empty;
            return;
        }

        _captureSettings.HotkeyEnabled = true;
        _captureSettings.HotkeyModifiers = (int)_proposedHotkeyModifiers;
        _captureSettings.HotkeyVirtualKey = (int)_proposedHotkeyVirtualKey;
        _captureSettings.HotkeyDisplayText = _proposedHotkeyText;
        CurrentHotkeyText.Text = _proposedHotkeyText;
        ProposedHotkeyText.Text = string.Empty;
        HotkeyRegistrationStatusText.Text = $"Registration status: Active ({_proposedHotkeyText})";
        await SaveSettingsQuietlyAsync();
    }

    private async void WindowSettingCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsUiEvents)
        {
            return;
        }

        _captureSettings.AlwaysOnTop = AlwaysOnTopSettingCheckBox.IsChecked == true;
        _captureSettings.RememberWindowPosition = RememberWindowPositionCheckBox.IsChecked == true;
        _captureSettings.PauseLivePreviewWhenHidden = PauseLivePreviewWhenHiddenCheckBox.IsChecked == true;
        _captureSettings.ContinueScannerWhileHidden = ContinueScannerWhileHiddenCheckBox.IsChecked == true;
        _captureSettings.ShowTrayNotificationAfterPayout = ShowTrayNotificationCheckBox.IsChecked == true;
        _captureSettings.ShowScannerPerformanceDiagnostics = ShowScannerPerformanceDiagnosticsCheckBox.IsChecked == true;
        _captureSettings.RequireQuickCaptureConfirmation = true;
        Topmost = _captureSettings.AlwaysOnTop;
        await SaveSettingsQuietlyAsync();
    }

    private async void ThemeComboBox_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressSettingsUiEvents ||
            ThemeComboBox.SelectedItem is not System.Windows.Controls.ComboBoxItem item ||
            item.Tag is not string themeName)
        {
            return;
        }

        _uiPreferences.Theme = _uiThemeService.Apply(themeName);
        UpdateDashboardStatus();
        WindowChrome.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0.72, 1, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });

        try
        {
            await _uiPreferencesService.SaveAsync(_uiPreferences);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            HotkeyRegistrationStatusText.Text = "The theme changed, but the UI preference could not be saved.";
        }
    }

    private async void SaveDiscordSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveDiscordConfigurationFromUiAsync(requireConfigured: false);
    }

    private async void TestDiscordWebhookButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await SaveDiscordConfigurationFromUiAsync(requireConfigured: true))
        {
            return;
        }

        SetDiscordControlsEnabled(false);
        SetDiscordStatus("Sending a test message...", null);
        try
        {
            var result = await _discordWebhookService.SendTestAsync(
                _discordWebhookUrl,
                _discordSettings.Username,
                _shutdownCancellation.Token);
            SetDiscordStatus(result.Message, result.Success);
        }
        finally
        {
            SetDiscordControlsEnabled(true);
        }
    }

    private async Task<bool> SaveDiscordConfigurationFromUiAsync(bool requireConfigured)
    {
        var username = DiscordUsernameTextBox.Text.Trim();
        var webhookUrl = DiscordWebhookPasswordBox.Password.Trim();
        var enabled = DiscordEnabledCheckBox.IsChecked == true;
        var isEmptyDisabledConfiguration = !requireConfigured &&
            !enabled &&
            string.IsNullOrWhiteSpace(username) &&
            string.IsNullOrWhiteSpace(webhookUrl);

        if (!isEmptyDisabledConfiguration)
        {
            if (string.IsNullOrWhiteSpace(username))
            {
                SetDiscordStatus("Add a username before configuring the Discord webhook.", false);
                DiscordUsernameTextBox.Focus();
                return false;
            }

            if (username.Contains('\r') || username.Contains('\n'))
            {
                SetDiscordStatus("The Discord username must be a single line.", false);
                DiscordUsernameTextBox.Focus();
                return false;
            }

            if (!DiscordWebhookService.IsValidWebhookUrl(webhookUrl))
            {
                SetDiscordStatus("Paste a valid Discord HTTPS webhook URL.", false);
                DiscordWebhookPasswordBox.Focus();
                return false;
            }
        }

        SetDiscordControlsEnabled(false);
        try
        {
            var protectedWebhookUrl = isEmptyDisabledConfiguration
                ? string.Empty
                : _windowsDataProtectionService.Protect(webhookUrl);
            var settings = new DiscordWebhookSettings
            {
                Username = username,
                Enabled = enabled && !isEmptyDisabledConfiguration,
                IncludeQuickScanScreenshot = DiscordIncludeScreenshotCheckBox.IsChecked == true,
                ProtectedWebhookUrl = protectedWebhookUrl
            };

            await _discordSettingsService.SaveAsync(settings);
            _discordSettings = settings;
            _discordWebhookUrl = isEmptyDisabledConfiguration ? string.Empty : webhookUrl;
            _discordSettingsLoadWarning = null;
            SetDiscordStatus(
                settings.Enabled
                    ? "Discord is enabled for confirmed Quick Scan payouts."
                    : string.IsNullOrWhiteSpace(settings.ProtectedWebhookUrl)
                        ? "Discord settings cleared."
                        : "Discord settings saved. Sending remains off.",
                true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            SetDiscordStatus("Discord settings could not be protected and saved locally.", false);
            return false;
        }
        finally
        {
            SetDiscordControlsEnabled(true);
        }
    }

    private void SetDiscordControlsEnabled(bool enabled)
    {
        DiscordUsernameTextBox.IsEnabled = enabled;
        DiscordWebhookPasswordBox.IsEnabled = enabled;
        DiscordEnabledCheckBox.IsEnabled = enabled;
        DiscordIncludeScreenshotCheckBox.IsEnabled = enabled;
        SaveDiscordSettingsButton.IsEnabled = enabled;
        TestDiscordWebhookButton.IsEnabled = enabled;
    }

    private void SetDiscordStatus(string message, bool? success)
    {
        DiscordStatusText.Text = message;
        DiscordStatusText.Foreground = success switch
        {
            true => GetThemeBrush("EarningsGreenBrush"),
            false => GetThemeBrush("ErrorRedBrush"),
            null => GetThemeBrush("SecondaryTextBrush")
        };
    }

    private async void ScannerDetectionModeComboBox_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressSettingsUiEvents ||
            ScannerDetectionModeComboBox.SelectedItem is not System.Windows.Controls.ComboBoxItem item ||
            item.Tag is not string modeText ||
            !Enum.TryParse<ScannerDetectionMode>(modeText, out var mode))
        {
            return;
        }

        _captureSettings.DetectionMode = mode;
        await SaveSettingsQuietlyAsync();
        if (AutomaticDetectionCheckBox.IsChecked == true &&
            _automaticDetectionCancellation is { IsCancellationRequested: false } cancellation &&
            GetAutomaticCaptureArea() is { } captureArea)
        {
            await _backgroundScannerService.StartAsync(
                captureArea,
                mode,
                cancellation.Token);
        }
    }

    private async void ScannerAreaModeComboBox_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressSettingsUiEvents ||
            ScannerAreaModeComboBox.SelectedItem is not System.Windows.Controls.ComboBoxItem item ||
            item.Tag is not string modeText ||
            !Enum.TryParse<ScannerAreaMode>(modeText, out var mode))
        {
            return;
        }

        _captureSettings.AreaMode = mode;
        UpdateCaptureAreaDisplay();
        await SaveSettingsQuietlyAsync();
        await RestartAutomaticDetectionForCaptureChangeAsync();
    }

    private async void QuickCaptureMonitorComboBox_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressSettingsUiEvents ||
            QuickCaptureMonitorComboBox.SelectedItem is not System.Windows.Controls.ComboBoxItem item ||
            item.Tag is not string monitorId)
        {
            return;
        }

        _captureSettings.QuickCaptureMonitorId = monitorId;
        UpdateCaptureAreaDisplay();
        await SaveSettingsQuietlyAsync();
        if (_captureSettings.AreaMode == ScannerAreaMode.AutoLocate)
        {
            await RestartAutomaticDetectionForCaptureChangeAsync();
        }
    }

    private async Task RestartAutomaticDetectionForCaptureChangeAsync()
    {
        if (AutomaticDetectionCheckBox.IsChecked != true ||
            _automaticDetectionCancellation is not { IsCancellationRequested: false } cancellation ||
            GetAutomaticCaptureArea() is not { } captureArea)
        {
            return;
        }

        await _backgroundScannerService.StartAsync(
            captureArea,
            _captureSettings.DetectionMode,
            cancellation.Token);
    }

    private async void StartCompactSettingCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsUiEvents)
        {
            return;
        }

        await SetWindowModeAsync(StartCompactSettingCheckBox.IsChecked == true);
    }

    private void Window_LocationChanged(object? sender, EventArgs e)
    {
        UpdateWindowCaptureBounds();
        if (!_isLoaded || !_captureSettings.RememberWindowPosition || WindowState != WindowState.Normal)
        {
            return;
        }

        RememberCurrentWindowPosition();
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateWindowCaptureBounds();
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        lock (_captureExclusionLock)
        {
            _windowVisibleForCapture = IsVisible && WindowState != WindowState.Minimized;
        }
        UpdateWindowCaptureBounds();
    }

    private void UpdateWindowCaptureBounds()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rectangle))
        {
            return;
        }

        lock (_captureExclusionLock)
        {
            _windowPhysicalBounds = new Int32Rect(
                rectangle.Left,
                rectangle.Top,
                Math.Max(1, rectangle.Right - rectangle.Left),
                Math.Max(1, rectangle.Bottom - rectangle.Top));
        }
    }

    private void RememberCurrentWindowPosition()
    {
        if (!_captureSettings.RememberWindowPosition || double.IsNaN(Left) || double.IsNaN(Top))
        {
            return;
        }

        _captureSettings.WindowLeft = Left;
        _captureSettings.WindowTop = Top;
    }

    private void RestoreWindowPosition()
    {
        if (!_captureSettings.RememberWindowPosition ||
            _captureSettings.WindowLeft is not double left ||
            _captureSettings.WindowTop is not double top ||
            double.IsNaN(left) ||
            double.IsNaN(top))
        {
            return;
        }

        Left = left;
        Top = top;
    }

    private async Task SaveSettingsQuietlyAsync()
    {
        try
        {
            await _captureSettingsService.SaveAsync(_captureSettings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            HotkeyRegistrationStatusText.Text = "Settings could not be saved locally.";
        }
    }

    private void ShowSingleKeyWarning(bool show)
    {
        HotkeyWarningText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        HotkeyWarningText.Text = show
            ? "This shortcut may interfere with the game or other applications. A shortcut containing Ctrl, Alt, or Shift is recommended."
            : string.Empty;
    }

    private static GlobalHotkeyModifiers ConvertModifiers(ModifierKeys modifiers)
    {
        var result = GlobalHotkeyModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            result |= GlobalHotkeyModifiers.Control;
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            result |= GlobalHotkeyModifiers.Alt;
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            result |= GlobalHotkeyModifiers.Shift;
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            result |= GlobalHotkeyModifiers.Windows;
        }

        return result;
    }

    private static string FormatHotkey(GlobalHotkeyModifiers modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(GlobalHotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(GlobalHotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(GlobalHotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(GlobalHotkeyModifiers.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(key == Key.Delete ? "Delete" : key.ToString());
        return string.Join(" + ", parts);
    }

    private static bool IsModifierKey(Key key)
    {
        return key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or
            Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;
    }

    private static TControl? FindVisualParent<TControl>(DependencyObject child)
        where TControl : DependencyObject
    {
        for (var current = child; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is TControl control)
            {
                return control;
            }
        }

        return null;
    }

    private async Task SetWindowModeAsync(bool isCompactMode)
    {
        if (_isModeTransitioning || _isCompactMode == isCompactMode)
        {
            return;
        }

        _isModeTransitioning = true;
        CompactModeTitleButton.IsEnabled = false;
        ExpandedModeTitleButton.IsEnabled = false;
        var targetWidth = isCompactMode ? CompactWindowWidth : 500d;
        var targetHeight = isCompactMode
            ? _pendingDetectedPayout is null ? CompactNormalHeight : CompactPendingHeight
            : 760d;
        ExpandedDashboard.Visibility = Visibility.Visible;
        CompactDashboard.Visibility = Visibility.Visible;
        var outgoing = isCompactMode ? ExpandedDashboard : CompactDashboard;
        var incoming = isCompactMode ? CompactDashboard : ExpandedDashboard;
        var direction = isCompactMode ? -1d : 1d;
        var outgoingTransform = (TranslateTransform)outgoing.RenderTransform;
        var incomingTransform = (TranslateTransform)incoming.RenderTransform;

        try
        {
            var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
            outgoing.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(130)) { EasingFunction = easeOut });
            outgoingTransform.BeginAnimation(
                TranslateTransform.XProperty,
                new DoubleAnimation(0, 18 * direction, TimeSpan.FromMilliseconds(150)) { EasingFunction = easeOut });

            await AnimateWindowSizeAsync(targetWidth, targetHeight);
            ApplyWindowMode(isCompactMode);
            incoming.Visibility = Visibility.Visible;
            incoming.Opacity = 0;
            incomingTransform.X = -24 * direction;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var incomingOpacity = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(210))
            {
                EasingFunction = easeOut
            };
            incomingOpacity.Completed += (_, _) => completion.TrySetResult();
            incoming.BeginAnimation(OpacityProperty, incomingOpacity);
            incomingTransform.BeginAnimation(
                TranslateTransform.XProperty,
                new DoubleAnimation(-24 * direction, 0, TimeSpan.FromMilliseconds(230)) { EasingFunction = easeOut });
            await completion.Task;

            outgoing.BeginAnimation(OpacityProperty, null);
            incoming.BeginAnimation(OpacityProperty, null);
            outgoingTransform.BeginAnimation(TranslateTransform.XProperty, null);
            incomingTransform.BeginAnimation(TranslateTransform.XProperty, null);
            outgoing.Opacity = 1;
            incoming.Opacity = 1;
            outgoingTransform.X = 0;
            incomingTransform.X = 0;
        }
        finally
        {
            _isModeTransitioning = false;
            CompactModeTitleButton.IsEnabled = true;
            ExpandedModeTitleButton.IsEnabled = true;
        }
        _captureSettings.IsCompactMode = isCompactMode;
        _suppressSettingsUiEvents = true;
        StartCompactSettingCheckBox.IsChecked = isCompactMode;
        _suppressSettingsUiEvents = false;

        try
        {
            await _captureSettingsService.SaveAsync(_captureSettings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!isCompactMode)
            {
                CaptureStatusText.Text = "Window mode changed, but the preference could not be saved.";
            }
        }
    }

    private void ApplyWindowMode(bool isCompactMode)
    {
        _isCompactMode = isCompactMode;
        ExpandedDashboard.Visibility = isCompactMode ? Visibility.Collapsed : Visibility.Visible;
        CompactDashboard.Visibility = isCompactMode ? Visibility.Visible : Visibility.Collapsed;
        NavigationBar.Visibility = isCompactMode ? Visibility.Collapsed : Visibility.Visible;
        CompactModeTitleButton.Visibility = isCompactMode ? Visibility.Collapsed : Visibility.Visible;
        ExpandedModeTitleButton.Visibility = isCompactMode ? Visibility.Visible : Visibility.Collapsed;
        ExpandedTitleSubtitle.Visibility = isCompactMode ? Visibility.Collapsed : Visibility.Visible;
        CompactTitleScannerPanel.Visibility = isCompactMode ? Visibility.Visible : Visibility.Collapsed;

        if (isCompactMode)
        {
            MinWidth = 400;
            MinHeight = 320;
            Width = CompactWindowWidth;
            Height = _pendingDetectedPayout is null ? CompactNormalHeight : CompactPendingHeight;
            MaxWidth = 430;
        }
        else
        {
            MaxWidth = 560;
            Width = 500;
            Height = 760;
            MinWidth = 460;
            MinHeight = 650;
            _ = Dispatcher.BeginInvoke(
                () => GetSelectedScrollViewer().ScrollToTop(),
                DispatcherPriority.ContextIdle);
        }

        KeepWindowInsideWorkArea();
    }

    private void AnimateWindowSize(double width, double height)
    {
        if (_isModeTransitioning)
        {
            return;
        }

        _ = AnimateWindowSizeAsync(width, height);
    }

    private async Task AnimateWindowSizeAsync(double width, double height)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MinWidth = 0;
        MinHeight = 0;
        MaxWidth = double.PositiveInfinity;
        var easing = new QuarticEase { EasingMode = EasingMode.EaseInOut };
        var widthAnimation = new DoubleAnimation(ActualWidth, width, TimeSpan.FromMilliseconds(340))
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };
        var heightAnimation = new DoubleAnimation(ActualHeight, height, TimeSpan.FromMilliseconds(340))
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };
        heightAnimation.Completed += (_, _) => completion.TrySetResult();
        BeginAnimation(WidthProperty, widthAnimation);
        BeginAnimation(HeightProperty, heightAnimation);
        await completion.Task;
        BeginAnimation(WidthProperty, null);
        BeginAnimation(HeightProperty, null);
        Width = width;
        Height = height;
        if (_isCompactMode || Math.Abs(width - CompactWindowWidth) < 1)
        {
            MinWidth = 400;
            MinHeight = 320;
            MaxWidth = 430;
        }
        else
        {
            MinWidth = 460;
            MinHeight = 650;
            MaxWidth = 560;
        }

        KeepWindowInsideWorkArea();
    }

    private void KeepWindowInsideWorkArea()
    {
        var workArea = GetCurrentMonitorWorkArea();
        var currentLeft = double.IsNaN(Left)
            ? workArea.Left + Math.Max(0, (workArea.Width - Width) / 2)
            : Left;
        var currentTop = double.IsNaN(Top)
            ? workArea.Top + Math.Max(0, (workArea.Height - Height) / 2)
            : Top;

        Left = Math.Clamp(currentLeft, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
        Top = Math.Clamp(currentTop, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height));
    }

    private Rect GetCurrentMonitorWorkArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return SystemParameters.WorkArea;
        }

        var physical = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null)
        {
            return new Rect(physical.X, physical.Y, physical.Width, physical.Height);
        }

        var transform = source.CompositionTarget.TransformFromDevice;
        var topLeft = transform.Transform(new System.Windows.Point(physical.Left, physical.Top));
        var bottomRight = transform.Transform(new System.Windows.Point(physical.Right, physical.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private Brush GetThemeBrush(string resourceKey)
    {
        return (Brush)FindResource(resourceKey);
    }

    private static string FormatLocalTimestamp(DateTime timestampUtc)
    {
        var localTimestamp = timestampUtc.ToLocalTime();
        return localTimestamp.Date == DateTime.Today
            ? $"Today, {localTimestamp.ToString("h:mm tt", CultureInfo.CurrentCulture)}"
            : localTimestamp.ToString("MMM d, h:mm tt", CultureInfo.CurrentCulture);
    }

    private static string FormatMoney(decimal amount)
    {
        return amount.ToString("$#,##0.##", MoneyCulture);
    }

    private static string GetDetectionMethodName(PayoutDetectionMethod detectionMethod)
    {
        return detectionMethod switch
        {
            PayoutDetectionMethod.TotalCashEarned => "Total Cash Earned",
            PayoutDetectionMethod.GtaTotalCashEarnedResultScreen => "GTA Total Cash Earned result screen",
            _ => "Plus-sign payout"
        };
    }

    private static bool IsTotalCashEarnedDetection(PayoutDetectionMethod detectionMethod)
    {
        return detectionMethod is
            PayoutDetectionMethod.TotalCashEarned or
            PayoutDetectionMethod.GtaTotalCashEarnedResultScreen;
    }

    private static string OneLine(string text)
    {
        return string.IsNullOrWhiteSpace(text)
            ? "(none)"
            : string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect rectangle);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
