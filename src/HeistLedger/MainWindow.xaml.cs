using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
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
    public const double CompactNormalHeight = 365;
    public const double CompactPendingHeight = 610;

    private static readonly CultureInfo MoneyCulture = CultureInfo.GetCultureInfo("en-US");
    private static readonly NumberStyles AcceptedAmountStyles =
        NumberStyles.AllowCurrencySymbol |
        NumberStyles.AllowThousands |
        NumberStyles.AllowDecimalPoint |
        NumberStyles.AllowLeadingWhite |
        NumberStyles.AllowTrailingWhite;

    private readonly EarningsStorageService _storageService = new();
    private readonly EarningsSessionService _earningsSessionService = new();
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
    private readonly PaddleOcrRecognitionService _paddleOcrService = new();
    private readonly PayoutDetectionService _payoutDetectionService;
    private readonly GtaLargeDigitReaderService _largeDigitReaderService;
    private readonly GreenCashPayoutDetectionService _greenCashPayoutDetectionService;
    private readonly ContinuousScreenCaptureService _continuousCaptureService;
    private readonly BackgroundScannerService _backgroundScannerService;
    private readonly PayoutDuplicateGuard _duplicateGuard = new();
    private readonly SemaphoreSlim _ocrScanSemaphore = new(1, 1);
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _countdownTimer;
    private readonly DispatcherTimer _livePreviewTimer;
    private readonly DispatcherTimer _quickCaptureNotificationTimer;
    private readonly DispatcherTimer _settingsSaveTimer;
    private readonly object _captureExclusionLock = new();
    private readonly List<PendingScanAttempt> _pendingScanAttempts = [];

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
    private GlobalHotkeyService? _minimizeHotkeyService;
    private TrayIconService? _trayIconService;
    private EarningRecord? _quickCaptureNotificationRecord;
    private bool _isClosing;
    private bool _isCompactMode;
    private bool _scannerPulseRunning;
    private bool _isLoaded;
    private bool _isRecordingHotkey;
    private bool _isModeTransitioning;
    private bool _isTabTransitioning;
    private string _requestedTab = "Dashboard";
    private bool _quickScanInProgress;
    private bool _previewCaptureInProgress;
    private bool _sessionsDirty = true;
    private EarningsSession _daySession = new(0, null, null);
    private EarningsSession _weekSession = new(0, null, null);
    private DateTime _sessionsCalculatedAtUtc;
    private int _resizeVersion;
    private TaskCompletionSource? _resizeCompletion;
    private bool _resumeLivePreviewAfterShow;
    private bool _resumeScannerAfterShow;
    private bool _suppressSettingsUiEvents;
    private bool _rescanInProgress;
    private DateTime _selectedHistoryDate = DateTime.Today;
    private DateTime _displayedHistoryMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime _selectedHistoryWeekStart = StartOfWeek(DateTime.Today);
    private string _selectedTab = "Dashboard";
    private GtaResultScreenDiagnostics? _latestGtaDiagnostics;
    private Int32Rect? _windowPhysicalBounds;
    private bool _windowVisibleForCapture = true;
    private GlobalHotkeyModifiers _proposedHotkeyModifiers =
        GlobalHotkeyModifiers.Control | GlobalHotkeyModifiers.Shift;
    private uint _proposedHotkeyVirtualKey = 0x48;
    private string _proposedHotkeyText = "Ctrl + Shift + H";
    private GlobalHotkeyModifiers _proposedMinimizeHotkeyModifiers =
        GlobalHotkeyModifiers.Control | GlobalHotkeyModifiers.Shift;
    private uint _proposedMinimizeHotkeyVirtualKey = 0x4D;
    private string _proposedMinimizeHotkeyText = "Ctrl + Shift + M";
    private HotkeyRecordingTarget _hotkeyRecordingTarget = HotkeyRecordingTarget.QuickScan;

    private sealed record PendingScanAttempt(long? Amount, int AgreeingReaders, bool Recognized);

    private enum HotkeyRecordingTarget
    {
        QuickScan,
        Minimize
    }

    public MainWindow()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => UpdatePresentationActivity();
        UpdateAmountPlaceholder();

        _payoutDetectionService = new PayoutDetectionService(
            _imagePreprocessingService,
            _ocrService,
            _headingOcrService,
            _amountOcrService);
        _largeDigitReaderService = new GtaLargeDigitReaderService(
            _payoutDetectionService,
            _amountOcrService,
            _paddleOcrService);
        _greenCashPayoutDetectionService = new GreenCashPayoutDetectionService(_amountOcrService);
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

        _countdownTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _countdownTimer.Tick += (_, _) => RefreshSessionDisplays(DateTime.UtcNow);

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
        _globalHotkeyService = new GlobalHotkeyService(windowHandle, 0x484C);
        _globalHotkeyService.HotkeyPressed += GlobalHotkeyService_HotkeyPressed;
        _minimizeHotkeyService = new GlobalHotkeyService(windowHandle, 0x484D);
        _minimizeHotkeyService.HotkeyPressed += MinimizeHotkeyService_HotkeyPressed;
        _trayIconService = new TrayIconService();
        _trayIconService.ShowRequested += TrayIconService_ShowRequested;
        _trayIconService.ExitRequested += TrayIconService_ExitRequested;
        UpdateWindowCaptureBounds();
    }

    private void PrepareLoadingExperience()
    {
        var hour = DateTime.Now.Hour;
        var greeting = hour < 12
            ? "Good morning"
            : hour < 18
                ? "Good afternoon"
                : "Good evening";
        GreetingPrefixText.Text = $"{greeting},";
        GreetingNameText.Text = " Player";
        LoadingStatusText.Text = "Preparing your next score…";
        LoadingOverlay.Visibility = Visibility.Visible;
        LoadingOverlay.Opacity = 1;
        LoadingContent.Opacity = 1;
        LoadingContentScale.ScaleX = 1;
        LoadingContentScale.ScaleY = 1;
        LoadingContentTranslate.Y = 0;

        // Staggered editorial reveal: emblem, wordmark, then the personal greeting.
        // Only opacity and transforms animate; the layout remains stationary.
        RevealIntroElement(LoadingEmblem, 0);
        RevealIntroElement(LoadingWordmark, 100);
        RevealIntroElement(LoadingGreeting, 230);
        LoadingProgressBar.BeginAnimation(WidthProperty,
            new DoubleAnimation(0, 350, TimeSpan.FromMilliseconds(
                SystemParameters.ClientAreaAnimation ? 1850 : 1))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        if (SystemParameters.ClientAreaAnimation)
        {
            LoadingProgressSheenTransform.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(-90, 370, TimeSpan.FromMilliseconds(1400))
                { RepeatBehavior = RepeatBehavior.Forever });
        }
    }

    private static void RevealIntroElement(FrameworkElement element, int delayMilliseconds)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        var delay = TimeSpan.FromMilliseconds(delayMilliseconds);
        var duration = TimeSpan.FromMilliseconds(650);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        element.Opacity = 0;
        var fade = new DoubleAnimation(0, 1, duration) { BeginTime = delay, EasingFunction = ease };
        element.BeginAnimation(OpacityProperty, fade);
        if (element.RenderTransform is TranslateTransform translation)
        {
            translation.Y = 22;
            translation.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(22, 0, duration) { BeginTime = delay, EasingFunction = ease });
        }
    }

    private async Task CompleteLoadingExperienceAsync()
    {
        var displayName = string.IsNullOrWhiteSpace(_discordSettings.Username)
            ? "Player"
            : _discordSettings.Username.Trim();
        var hour = DateTime.Now.Hour;
        var greeting = hour < 12
            ? "Good morning"
            : hour < 18
                ? "Good afternoon"
                : "Good evening";
        GreetingPrefixText.Text = $"{greeting},";
        GreetingNameText.Text = $" {displayName}";
        LoadingStatusText.Text = "Loaded — enjoy your session.";
        await Task.Delay(1900);
        if (_isClosing) return;

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 480 : 1))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        fade.Completed += (_, _) => completion.TrySetResult();
        LoadingContentTranslate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, -28, fade.Duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
        LoadingOverlay.BeginAnimation(OpacityProperty, fade);
        await completion.Task;
        LoadingOverlay.Visibility = Visibility.Collapsed;
        LoadingOverlay.BeginAnimation(OpacityProperty, null);
        LoadingOverlay.Opacity = 1;
        LoadingProgressSheenTransform.BeginAnimation(TranslateTransform.XProperty, null);
        LoadingProgressBar.BeginAnimation(WidthProperty, null);
        LoadingContentTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        foreach (var element in new FrameworkElement[] { LoadingEmblem, LoadingWordmark, LoadingGreeting })
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = 1;
            if (element.RenderTransform is TranslateTransform translation)
            {
                translation.BeginAnimation(TranslateTransform.YProperty, null);
                translation.Y = 0;
            }
        }
    }

    private void StartAmbientAnimations()
    {
        StopAmbientAnimations();
        if (_isClosing || _isCompactMode || !IsVisible || WindowState == WindowState.Minimized ||
            _quickScanInProgress || !SystemParameters.ClientAreaAnimation) return;

        // A short atmospheric entrance settles to a static background. An idle
        // overlay must not redraw the entire layered window sixty times a second.
        var drift = new DoubleAnimation(0, 14, TimeSpan.FromSeconds(2.5))
        {
            AutoReverse = true,
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Timeline.SetDesiredFrameRate(drift, 20);
        AmbientGlowOneTransform.BeginAnimation(TranslateTransform.YProperty, drift);
        AmbientGlowTwoTransform.BeginAnimation(TranslateTransform.XProperty, drift);
    }

    private void StopAmbientAnimations()
    {
        AmbientGlowOneTransform.BeginAnimation(TranslateTransform.XProperty, null);
        AmbientGlowOneTransform.BeginAnimation(TranslateTransform.YProperty, null);
        AmbientGlowTwoTransform.BeginAnimation(TranslateTransform.XProperty, null);
        AmbientGlowTwoTransform.BeginAnimation(TranslateTransform.YProperty, null);
        AmbientGridTransform.BeginAnimation(TranslateTransform.XProperty, null);
        AmbientGridTransform.BeginAnimation(TranslateTransform.YProperty, null);
    }

    private void UpdatePresentationActivity()
    {
        if (!_isLoaded || _isClosing) return;
        if (IsVisible && WindowState != WindowState.Minimized)
        {
            RefreshSessionDisplays(DateTime.UtcNow);
            _countdownTimer.Start();
            _refreshTimer.Start();
            if (_resumeLivePreviewAfterShow && LivePreviewCheckBox.IsChecked == true)
            {
                _resumeLivePreviewAfterShow = false;
                _livePreviewTimer.Start();
            }
        }
        else
        {
            _countdownTimer.Stop();
            _refreshTimer.Stop();
            if (_livePreviewTimer.IsEnabled)
            {
                _resumeLivePreviewAfterShow = true;
                _livePreviewTimer.Stop();
            }
            StopAmbientAnimations();
        }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        PrepareLoadingExperience();
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
        _captureSettings = savedCaptureSettings;
        RefreshPrimaryCaptureArea();
        _paddleOcrService.Enabled = _captureSettings.PaddleOcrEnabled;

        Topmost = _captureSettings.AlwaysOnTop;
        RestoreWindowPosition();
        LoadSettingsControls();
        InitializeUpdatesUi();
        SelectTab(
            _captureSettings.LastSelectedTab.Equals("Settings", StringComparison.OrdinalIgnoreCase)
                ? "Settings"
                : "Dashboard",
            saveSelection: false);
        ApplyWindowMode(_captureSettings.IsCompactMode);
        RefreshCalendar();
        UpdateCaptureAreaDisplay();
        RefreshTotals();
        UpdateDashboardStatus();
        RegisterConfiguredHotkey();
        _refreshTimer.Start();
        _countdownTimer.Start();
        _isLoaded = true;
        _ = InitializePaddleOcrStatusAsync();
        AutomaticDetectionCheckBox.IsChecked = false;
        UpdateWindowCaptureBounds();
        ShowNormalInterface();

        if (!_isCompactMode)
        {
            _ = Dispatcher.BeginInvoke(
                () => GetSelectedScrollViewer().ScrollToTop(),
                DispatcherPriority.ContextIdle);
        }

        StartAmbientAnimations();
        await CompleteLoadingExperienceAsync();
        if (_captureSettings.CheckForUpdatesOnStartup && !_isClosing)
            _ = CheckAppUpdatesAsync(automatic: true);
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
        _updateDownloadCancellation?.Cancel();
        StopAmbientAnimations();
        _resizeCompletion?.TrySetResult();
        RememberCurrentWindowPosition();
        _refreshTimer.Stop();
        _countdownTimer.Stop();
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

        if (_minimizeHotkeyService is not null)
        {
            _minimizeHotkeyService.HotkeyPressed -= MinimizeHotkeyService_HotkeyPressed;
            _minimizeHotkeyService.Dispose();
            _minimizeHotkeyService = null;
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

        _paddleOcrService.Dispose();

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

    private void SelectCaptureAreaButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshPrimaryCaptureArea();
        UpdateCaptureAreaDisplay();
    }

    private async void TestCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        await CaptureSelectedAreaAsync(showSuccessStatus: true);
    }

    private async void LivePreviewCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        if (!HasUsableScreen())
        {
            CaptureStatusText.Text = "No capture area selected.";
            LivePreviewCheckBox.IsChecked = false;
            return;
        }

        await CaptureSelectedAreaAsync(showSuccessStatus: true);
        if (HasUsableScreen() &&
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
        SettingsCapturePreviewImage.Source = null;
        SettingsCapturePreviewPlaceholder.Visibility = Visibility.Visible;
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

    private async void LivePreviewTimer_Tick(object? sender, EventArgs e)
    {
        if (!IsVisible || WindowState == WindowState.Minimized || _quickScanInProgress) return;
        await CaptureSelectedAreaAsync(showSuccessStatus: false);
    }

    private async Task CaptureSelectedAreaAsync(bool showSuccessStatus, bool forSettings = false)
    {
        if (_previewCaptureInProgress || _quickScanInProgress || _isClosing) return;
        if (!HasUsableScreen())
        {
            _livePreviewTimer.Stop();
            LivePreviewCheckBox.IsChecked = false;
            CaptureStatusText.Text = "No capture area selected.";
            return;
        }

        var bounds = new CaptureSettings
        {
            X = _captureSettings.X, Y = _captureSettings.Y,
            Width = _captureSettings.Width, Height = _captureSettings.Height
        };
        _previewCaptureInProgress = true;
        try
        {
            var preview = await Task.Run(() => _screenCaptureService.Capture(bounds), _shutdownCancellation.Token);
            // A region can be cleared or reselected while this worker finishes.
            if (_isClosing || !_captureSettings.HasCaptureArea ||
                bounds.X != _captureSettings.X || bounds.Y != _captureSettings.Y ||
                bounds.Width != _captureSettings.Width || bounds.Height != _captureSettings.Height) return;
            if (forSettings)
            {
                SettingsCapturePreviewImage.Source = preview;
                SettingsCapturePreviewPlaceholder.Visibility = Visibility.Collapsed;
            }
            else
            {
                CapturePreviewImage.Source = preview;
                CapturePreviewPlaceholderText.Visibility = Visibility.Collapsed;
            }
            if (showSuccessStatus)
            {
                CaptureStatusText.Text = "Capture successful.";
            }
        }
        catch (OperationCanceledException) when (_isClosing) { }
        catch (Exception)
        {
            if (!_isClosing) CaptureStatusText.Text = "Capture failed.";
        }
        finally { _previewCaptureInProgress = false; }
    }

    private void UpdateCaptureAreaDisplay()
    {
        var hasArea = HasUsableScreen();
        CaptureAreaText.Text = hasArea ? $"Primary display: {_captureSettings.Width} × {_captureSettings.Height}" : "Primary display unavailable";
        SettingsCaptureAreaText.Text = CaptureAreaText.Text;
        UpdateDashboardStatus();
    }

    private void ShowCaptureSetupState()
    {
        ShowNormalInterface();
        CaptureStatusText.Text = "Primary display unavailable. Reconnect the display and try again.";
    }

    private void ShowNormalInterface()
    {
        CaptureSetupPanel.Visibility = Visibility.Collapsed;
        ApplyWindowMode(_isCompactMode);
        ExpandedDashboard.Visibility = _isCompactMode ? Visibility.Collapsed : Visibility.Visible;
        CompactDashboard.Visibility = _isCompactMode ? Visibility.Visible : Visibility.Collapsed;
        var ready = HasUsableScreen();
        QuickCaptureButton.IsEnabled = ready;
        CompactQuickCaptureButton.IsEnabled = ready;
    }

    private async void AnalyzeCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        await RunOcrScanAsync(_shutdownCancellation.Token);
    }

    private async void QuickCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        await RunQuickCaptureAsync(_shutdownCancellation.Token, ScanRequestMode.Normal);
    }

    private async Task RunQuickCaptureAsync(
        CancellationToken cancellationToken,
        ScanRequestMode requestMode = ScanRequestMode.Normal)
    {
        // Repeated hotkey presses must not queue multiple expensive OCR bursts.
        if (_quickScanInProgress || _isClosing) return;
        if (!HasUsableScreen())
        {
            ShowCaptureSetupState();
            ShowQuickCaptureStatusNotification("Primary display unavailable.");
            return;
        }

        var forcedRescan = requestMode == ScanRequestMode.ForcedRescan;
        var previousCandidate = forcedRescan ? _pendingDetectedPayout : null;
        if (forcedRescan && (previousCandidate is null || _rescanInProgress))
        {
            return;
        }

        if (forcedRescan)
        {
            _rescanInProgress = true;
            SetPendingResultButtonsEnabled(false);
            ExpandedRescanButton.Content = "RESCANNING…";
            CompactRescanButton.Content = "RESCANNING…";
            ShowPreviousResult(previousCandidate!, "Previous result retained while a fresh five-frame scan runs.");
            ConfirmationVerificationStatusText.Text = "RESCANNING…";
            CompactVerificationStatusText.Text = "RESCANNING…";
        }

        _quickScanInProgress = true;
        StopAmbientAnimations();
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
                await Dispatcher.Yield(DispatcherPriority.Render);
                await Task.Run(ScreenCaptureService.WaitForDesktopComposition, cancellationToken);
            }

            var frames = (await Task.Run(
                () => CaptureBurst(selectedBounds, GtaLargeDigitReaderService.MaximumBurstFrames, cancellationToken),
                cancellationToken)).ToList();

            if (shouldRestoreWindow)
            {
                ShowExistingMainWindow();
                shouldRestoreWindow = false;
            }

            SetQuickScanState("Finding payout…", "ANALYZING…");
            var resultRegion = await Task.Run(() => _payoutDetectionService.LocateFullScreenResult(frames[0], cancellationToken), cancellationToken);
            frames = await Task.Run(() => FullScreenScanService.CropFrames(frames, resultRegion), cancellationToken);
            var greenIconAnalysis = resultRegion is not null ? null : await Task.Run(
                () => _greenCashPayoutDetectionService.Analyze(frames, cancellationToken, fullScreen: true), cancellationToken);
            var selectedResult = greenIconAnalysis is not null
                ? new BurstPayoutAnalysisResult(
                    greenIconAnalysis,
                    frames[0],
                    frames.Count,
                    greenIconAnalysis.GtaResultDiagnostics?.StableFrameCount ?? 1)
                : await Task.Run(
                    () => _largeDigitReaderService.Analyze(
                        frames,
                        _captureSettings.DetectionMode,
                        cancellationToken,
                        forceTargetedVerification: true),
                    cancellationToken);

            if (greenIconAnalysis is null && selectedResult.NeedsTargetedFallback)
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
                    await Dispatcher.Yield(DispatcherPriority.Render);
                    await Task.Run(ScreenCaptureService.WaitForDesktopComposition, cancellationToken);
                }

                var additionalFrames = await Task.Run(
                    () => CaptureBurst(selectedBounds, 2, cancellationToken),
                    cancellationToken);
                frames.AddRange(await Task.Run(() => FullScreenScanService.CropFrames(additionalFrames, resultRegion), cancellationToken));
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
            if (forcedRescan && analysis.GtaResultDiagnostics is not null)
            {
                analysis = analysis with
                {
                    GtaResultDiagnostics = analysis.GtaResultDiagnostics with { ForcedRescan = true }
                };
            }
            SetQuickScanState("Reading amount…", "ANALYZING…");
            CapturePreviewImage.Source = selectedResult.PreviewFrame;
            CapturePreviewPlaceholderText.Visibility = Visibility.Collapsed;
            UpdateOcrDiagnostics(analysis);
            await HandleQuickCaptureAnalysisAsync(analysis, requestMode, previousCandidate);
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
            _quickScanInProgress = false;
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
                if (forcedRescan)
                {
                    _rescanInProgress = false;
                    ExpandedRescanButton.Content = "RESCAN";
                    CompactRescanButton.Content = "RESCAN";
                    SetPendingResultButtonsEnabled(true);
                }
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
        using var captureSession = _screenCaptureService.OpenSession(bounds);
        for (var index = 0; index < frameCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            frames.Add(captureSession.CaptureFrame());
            if (index + 1 < frameCount)
            {
                cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(50));
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

    private async Task HandleQuickCaptureAnalysisAsync(
        PayoutAnalysisResult analysis,
        ScanRequestMode requestMode,
        DetectedPayout? previousCandidate)
    {
        var candidate = analysis.Candidate;
        if (candidate is null)
        {
            if (requestMode == ScanRequestMode.ForcedRescan && previousCandidate is not null)
            {
                _pendingDetectedPayout = previousCandidate;
                RecordFailedScanAttempt();
                ShowPreviousResult(previousCandidate, "No new payout result was recognized.");
                SetVerificationStatus(previousCandidate, _latestGtaDiagnostics);
                PayoutConfirmationCard.Visibility = Visibility.Visible;
                CompactPendingIndicator.Visibility = Visibility.Visible;
                CompactNormalPanel.Visibility = Visibility.Collapsed;
                QuickCaptureStatusText.Text = "No new payout result was recognized.";
                OcrStatusText.Text = "No new payout result was recognized.";
                DiagnosticStatusText.Text = "Scan status: Forced rescan found no new candidate";
                return;
            }

            _duplicateGuard.RecordNoCandidate(analysis.ImageFingerprint);
            var anchorMissing = analysis.GtaResultDiagnostics?.AnchorFound != true;
            var message = anchorMissing
                ? "No supported payout was found. Include either TOTAL CASH EARNED or the green cash icon and full amount."
                : "A payout marker was found, but its complete amount could not be read reliably.";
            SetQuickScanState("No result found", "QUICK SCAN");
            QuickCaptureStatusText.Text = message;
            OcrStatusText.Text = message;
            DiagnosticStatusText.Text = "Scan status: Quick Capture found no anchored payout";
            ShowQuickCaptureStatusNotification(message);
            ShowCompactScanMessage(
                "No payout found\nInclude the full heading or green cash icon together with the complete amount.");
            return;
        }

        if (_pendingDetectedPayout is not null && requestMode == ScanRequestMode.Normal)
        {
            QuickCaptureStatusText.Text = "A payout is already waiting for confirmation.";
            ShowQuickCaptureStatusNotification("A payout is already waiting for confirmation.");
            return;
        }

        if (!_duplicateGuard.ShouldPresent(candidate, DateTime.UtcNow, requestMode))
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
            QuickCaptureStatusText.Text = candidate.DetectionMethod == PayoutDetectionMethod.GreenCashIconPayout
                ? "A green-icon cash payout needs confirmation."
                : "A plus-sign payout needs confirmation.";
            ShowQuickCaptureStatusNotification("Payout needs confirmation.");
            return;
        }

        PresentPayoutCandidate(candidate, "Quick Scan");
        ExpandedPreviousResultBorder.Visibility = Visibility.Collapsed;
        CompactPreviousResultBorder.Visibility = Visibility.Collapsed;
        SetQuickScanState("Verify detected amount", "QUICK SCAN");
        QuickCaptureStatusText.Text = candidate.IsAmbiguous
            ? "Reader results disagree. Check or edit the amount before saving."
            : "Check the detected amount, then accept, edit, rescan, or reject it.";
        OcrStatusText.Text = "Quick Scan result is waiting for your confirmation.";
        DiagnosticStatusText.Text = "Scan status: Quick Scan awaiting manual verification";
        ShowQuickCaptureStatusNotification("Verify the detected payout before saving.");
        await Task.CompletedTask;
    }

    private async void RescanPayoutButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingDetectedPayout is null || _rescanInProgress)
        {
            return;
        }

        await RunQuickCaptureAsync(_shutdownCancellation.Token, ScanRequestMode.ForcedRescan);
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
        return HasUsableScreen()
            ? new CaptureSettings
            {
                X = _captureSettings.X,
                Y = _captureSettings.Y,
                Width = _captureSettings.Width,
                Height = _captureSettings.Height
            }
            : null;
    }

    private void RefreshPrimaryCaptureArea()
    {
        var screen = _screenCaptureService.GetPrimaryScreenBounds();
        _captureSettings.X = screen.X;
        _captureSettings.Y = screen.Y;
        _captureSettings.Width = screen.Width;
        _captureSettings.Height = screen.Height;
        _captureSettings.AreaMode = ScannerAreaMode.FullScreen;
        _captureSettings.CaptureAreaConfirmed = true;
        _captureSettings.QuickCaptureMonitorId = "Primary";
    }

    private bool HasUsableScreen()
    {
        RefreshPrimaryCaptureArea();
        return _screenCaptureService.IsValidCaptureArea(_captureSettings);
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
            if (!_duplicateGuard.CanSave(candidate))
            {
                return;
            }

            var saved = await SaveEarningAmountAsync(candidate.Amount, "Automatic Detection");
            if (saved is null)
            {
                return;
            }

            _duplicateGuard.RecordSaved(candidate, DateTime.UtcNow);
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
        if (!HasUsableScreen())
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
            $"Forced rescan: {(gta.ForcedRescan ? "Yes" : "No")}",
            $"Independent readers: {gta.IndependentReaderAgreement}",
            $"Tesseract: {(gta.TesseractAmount is long tesseractAmount ? FormatMoney(tesseractAmount) : "No valid candidate")}",
            $"PaddleOCR: {(gta.PaddleOcrAmount is long paddleAmount ? FormatMoney(paddleAmount) : gta.PaddleOcrStatus)}",
            $"Digit reader: {(gta.DigitReaderAmount is long digitAmount ? FormatMoney(digitAmount) : "No valid candidate")}",
            $"PaddleOCR processing: {(gta.PaddleOcrProcessingMilliseconds is double paddleMs ? $"{paddleMs:0.0} ms" : "--")}",
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

        UpdatePaddleOcrStatus();

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
        var isGreenCashIcon = candidate.DetectionMethod == PayoutDetectionMethod.GreenCashIconPayout;
        ConfirmationHeadingText.Text = isGreenCashIcon
            ? "GREEN CASH PAYOUT — VERIFY BEFORE SAVING"
            : isTotalCashEarned
                ? "PAYOUT FOUND — VERIFY BEFORE SAVING"
                : "PAYOUT REQUIRES CONFIRMATION";
        DetectedPayoutText.Text = isTotalCashEarned || isGreenCashIcon
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
                FormatReaderResult("Tesseract", diagnostics.TesseractAmount),
                FormatReaderResult("PaddleOCR", diagnostics.PaddleOcrAmount),
                FormatReaderResult("Digit reader", diagnostics.DigitReaderAmount),
                $"Readers agreeing: {diagnostics.IndependentReaderAgreement} of 3",
                $"Digit count: {candidate.Amount.ToString(CultureInfo.InvariantCulture).Length}",
                $"Competing candidates: {diagnostics.CompetingCandidates}",
                $"Uncertain digit positions: {diagnostics.UncertainDigitPositions}");
        ConfirmationAmountCropImage.Source = diagnostics?.AmountCropPreview;
        ConfirmationPreviewPanel.Visibility = diagnostics?.AmountCropPreview is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        PayoutConfirmationCard.Visibility = Visibility.Visible;
        CompactPendingIndicator.Visibility = Visibility.Visible;
        CompactNormalPanel.Visibility = Visibility.Collapsed;
        CompactEditPanel.Visibility = Visibility.Collapsed;
        CompactPendingText.Text = FormatMoney(candidate.Amount);
        CompactPendingMetaText.Text =
            $"Confidence: {QuickCapturePolicy.NormalizeConfidence(candidate.Confidence):P0}  •  Readers agreeing: {diagnostics?.IndependentReaderAgreement ?? 0} of 3";
        CompactPendingConflictText.Text = FormatPossibleAlternatives(candidate.Amount, diagnostics);
        CompactPendingPreviewImage.Source = diagnostics?.AmountCropPreview;
        SetVerificationStatus(candidate, diagnostics);
        RecordScanAttempt(candidate, diagnostics?.IndependentReaderAgreement ?? 0);
        ExpandedEditPanel.Visibility = Visibility.Collapsed;
        OcrStatusText.Text = "Payout candidate detected. Accept, edit, rescan, or reject it.";
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

    private void SetPendingResultButtonsEnabled(bool enabled)
    {
        ExpandedAcceptButton.IsEnabled = enabled;
        ExpandedEditButton.IsEnabled = enabled;
        ExpandedRescanButton.IsEnabled = enabled;
        ExpandedRejectButton.IsEnabled = enabled;
        CompactAcceptButton.IsEnabled = enabled;
        CompactEditButton.IsEnabled = enabled;
        CompactRescanButton.IsEnabled = enabled;
        CompactRejectButton.IsEnabled = enabled;
    }

    private static string FormatReaderResult(string label, long? amount)
    {
        return amount is long value
            ? $"{label}: {FormatMoney(value)} ({value.ToString(CultureInfo.InvariantCulture).Length} digits)"
            : $"{label}: No valid result";
    }

    private static string FormatPossibleAlternatives(
        long selectedAmount,
        GtaResultScreenDiagnostics? diagnostics)
    {
        if (diagnostics is null)
        {
            return "Possible alternatives: None";
        }

        var alternatives = new[]
        {
            diagnostics.TesseractAmount,
            diagnostics.PaddleOcrAmount,
            diagnostics.DigitReaderAmount
        }
            .Where(amount => amount is not null && amount.Value != selectedAmount)
            .Select(amount => amount!.Value)
            .Distinct()
            .Select(amount => FormatMoney(amount))
            .ToList();
        if (diagnostics.CompetingCandidates != "None")
        {
            alternatives.Add(diagnostics.CompetingCandidates);
        }

        return alternatives.Count == 0
            ? $"Possible alternatives: None • Uncertain positions: {diagnostics.UncertainDigitPositions}"
            : $"Possible alternatives: {string.Join(", ", alternatives)} • Uncertain positions: {diagnostics.UncertainDigitPositions}";
    }

    private void SetVerificationStatus(DetectedPayout candidate, GtaResultScreenDiagnostics? diagnostics)
    {
        var confidence = QuickCapturePolicy.NormalizeConfidence(candidate.Confidence);
        var engineAmounts = new[]
        {
            diagnostics?.TesseractAmount,
            diagnostics?.PaddleOcrAmount,
            diagnostics?.DigitReaderAmount
        }.Where(amount => amount is not null).Select(amount => amount!.Value).Distinct().ToArray();
        var hasConflict = engineAmounts.Length > 1 ||
            diagnostics is not null && diagnostics.CompetingCandidates != "None";
        var status = engineAmounts.Length == 0
            ? "INCOMPLETE READING"
            : hasConflict
                ? "CONFLICTING READERS"
                : diagnostics?.IndependentReaderAgreement != 3
                    ? "VERIFICATION REQUIRED"
                    : confidence < QuickCapturePolicy.AutomaticAddConfidenceThreshold
                        ? "LOW CONFIDENCE"
                        : diagnostics.UncertainDigitPositions == "None"
                            ? "STRONG AGREEMENT"
                            : "VERIFICATION REQUIRED";
        var strong = status == "STRONG AGREEMENT";
        var brush = GetThemeBrush(strong ? "EarningsGreenBrush" : "WarningGoldBrush");
        ConfirmationVerificationStatusText.Text = status;
        ConfirmationVerificationStatusText.Foreground = brush;
        CompactVerificationStatusText.Text = status;
        CompactVerificationStatusText.Foreground = brush;
        ConfirmationHeadingText.Foreground = brush;
        CompactPendingHeadingText.Foreground = brush;
    }

    private void RecordScanAttempt(DetectedPayout candidate, int agreeingReaders)
    {
        _pendingScanAttempts.Add(new PendingScanAttempt(candidate.Amount, agreeingReaders, true));
        TrimAndRenderScanAttempts();
    }

    private void RecordFailedScanAttempt()
    {
        _pendingScanAttempts.Add(new PendingScanAttempt(null, 0, false));
        TrimAndRenderScanAttempts();
    }

    private void TrimAndRenderScanAttempts()
    {
        while (_pendingScanAttempts.Count > 3)
        {
            _pendingScanAttempts.RemoveAt(0);
        }

        var lines = _pendingScanAttempts.Select((attempt, index) => attempt.Recognized
            ? $"Attempt {index + 1}: {FormatMoney(attempt.Amount!.Value)} — {attempt.AgreeingReaders}/3 readers"
            : $"Attempt {index + 1}: No result recognized");
        var text = "SCAN ATTEMPTS" + Environment.NewLine + string.Join(Environment.NewLine, lines);
        ExpandedScanAttemptsText.Text = text;
        ExpandedScanAttemptsText.Visibility = Visibility.Visible;
        CompactScanAttemptsText.Text = text;
        CompactScanAttemptsText.Visibility = Visibility.Visible;
    }

    private void ShowPreviousResult(DetectedPayout candidate, string message)
    {
        var text = $"PREVIOUS RESULT: {FormatMoney(candidate.Amount)}{Environment.NewLine}{message}";
        ExpandedPreviousResultText.Text = text;
        ExpandedPreviousResultBorder.Visibility = Visibility.Visible;
        CompactPreviousResultText.Text = text;
        CompactPreviousResultBorder.Visibility = Visibility.Visible;
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
            if (!_duplicateGuard.CanSave(pendingPayout))
            {
                OcrStatusText.Text = "This visible payout was already saved. Rescan will not add it twice.";
                DiagnosticStatusText.Text = "Scan status: Saved-earning duplicate blocked";
                return;
            }

            var source = _pendingPayoutSource;
            var screenshotPng = _pendingPayoutScreenshotPng;
            var savedRecord = await SaveEarningAmountAsync(pendingPayout.Amount, source);
            if (savedRecord is null)
            {
                OcrStatusText.Text = "The detected payout could not be saved.";
                return;
            }

            _duplicateGuard.RecordSaved(pendingPayout, DateTime.UtcNow);
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
            CompactEditPreviewImage.Source = CompactPendingPreviewImage.Source;
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
        var savedCandidate = pending with
        {
            Amount = (long)amount,
            NormalizedOcrText = amount.ToString(CultureInfo.InvariantCulture)
        };
        if (!_duplicateGuard.CanSave(savedCandidate))
        {
            OcrStatusText.Text = "This visible payout and amount were already saved.";
            return;
        }

        var savedRecord = await SaveEarningAmountAsync(amount, source);
        if (savedRecord is null)
        {
            return;
        }

        _duplicateGuard.RecordSaved(savedCandidate, DateTime.UtcNow);
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
        CompactEditPreviewImage.Source = null;
        ExpandedEditPanel.Visibility = Visibility.Collapsed;
        ExpandedPreviousResultBorder.Visibility = Visibility.Collapsed;
        CompactPreviousResultBorder.Visibility = Visibility.Collapsed;
        ExpandedScanAttemptsText.Visibility = Visibility.Collapsed;
        CompactScanAttemptsText.Visibility = Visibility.Collapsed;
        _pendingScanAttempts.Clear();
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
        var last24Hours = _earningsSessionService.GetCurrentSession(
            _earnings,
            TimeSpan.FromHours(24),
            nowUtc).Total;
        var last7Days = _earningsSessionService.GetCurrentSession(
            _earnings,
            TimeSpan.FromDays(7),
            nowUtc).Total;
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
        var canScan = !isProcessing && HasUsableScreen();
        QuickCaptureButton.IsEnabled = canScan;
        CompactQuickCaptureButton.IsEnabled = canScan;
        if (!isProcessing)
        {
            QuickCaptureButton.Content = "SCAN PAYOUT";
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
        _sessionsDirty = true;
        var nowUtc = DateTime.UtcNow;
        var latestRecord = _earnings.MaxBy(record => record.TimestampUtc);
        var lastPayout = latestRecord?.Amount ?? 0;
        var displayName = string.IsNullOrWhiteSpace(_discordSettings.Username)
            ? "Player"
            : _discordSettings.Username.Trim();
        var greeting = DateTime.Now.Hour < 12
            ? "Good morning"
            : DateTime.Now.Hour < 18
                ? "Good afternoon"
                : "Good evening";

        DashboardGreetingPrefixText.Text = $"{greeting},";
        DashboardGreetingNameText.Text = $" {displayName}";
        UsernameTipBorder.Visibility = string.IsNullOrWhiteSpace(_discordSettings.Username)
            ? Visibility.Visible
            : Visibility.Collapsed;
        DashboardDateText.Text =
            $"{DateTime.Now:dddd, MMMM d}  •  Your ledger is ready for the next score.";

        LastPayoutText.Text = FormatMoney(lastPayout);
        HeroLastPayoutText.Text = FormatMoney(lastPayout);
        CompactLastPayoutText.Text = FormatMoney(lastPayout);
        LastPayoutTimestampText.Text = latestRecord is null
            ? "No transactions yet"
            : FormatLocalTimestamp(latestRecord.TimestampUtc);
        LastUpdatedText.Text = $"Last updated: {DateTime.Now.ToString("h:mm tt", CultureInfo.CurrentCulture)}";
        UndoLastButton.IsEnabled = _earnings.Count > 0 && AddEarningButton.IsEnabled;
        RecentUndoButton.IsEnabled = _earnings.Count > 0 && AddEarningButton.IsEnabled;
        RefreshSessionDisplays(nowUtc);
        RefreshWeeklyChart();
        RefreshCalendar();
        RefreshRecentActivity();
    }

    private async void PreviewSettingsCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (!HasUsableScreen())
        {
            SettingsCapturePreviewImage.Source = null;
            SettingsCapturePreviewPlaceholder.Visibility = Visibility.Visible;
            CaptureStatusText.Text = "No capture area selected.";
            return;
        }

        await CaptureSelectedAreaAsync(showSuccessStatus: true, forSettings: true);
    }

    private void RefreshSessionDisplays(DateTime nowUtc)
    {
        if (_sessionsDirty || nowUtc < _sessionsCalculatedAtUtc ||
            _daySession.EndUtc is DateTime dayEnd && nowUtc >= dayEnd ||
            _weekSession.EndUtc is DateTime weekEnd && nowUtc >= weekEnd)
        {
            _daySession = _earningsSessionService.GetCurrentSession(_earnings, TimeSpan.FromHours(24), nowUtc);
            _weekSession = _earningsSessionService.GetCurrentSession(_earnings, TimeSpan.FromDays(7), nowUtc);
            _sessionsCalculatedAtUtc = nowUtc;
            _sessionsDirty = false;
        }
        var daySession = _daySession;
        var weekSession = _weekSession;

        DayTotalText.Text = FormatMoney(daySession.Total);
        WeekTotalText.Text = FormatMoney(weekSession.Total);
        CompactDayTotalText.Text = FormatMoney(daySession.Total);
        CompactWeekTotalText.Text = FormatMoney(weekSession.Total);
        DayCountdownText.Text = FormatSessionCountdown(daySession, nowUtc, includeDays: false);
        WeekCountdownText.Text = FormatSessionCountdown(weekSession, nowUtc, includeDays: true);
        CompactDayCountdownText.Text = FormatSessionCountdown(daySession, nowUtc, includeDays: false);
        CompactWeekCountdownText.Text = FormatSessionCountdown(weekSession, nowUtc, includeDays: true);
    }

    private static string FormatSessionCountdown(
        EarningsSession session,
        DateTime nowUtc,
        bool includeDays)
    {
        if (!session.IsActive || session.EndUtc is null)
        {
            return "Starts with your next payout";
        }

        var remaining = session.EndUtc.Value - nowUtc;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        return includeDays
            ? $"Resets in {remaining.Days}d {remaining.Hours:00}h {remaining.Minutes:00}m"
            : $"Resets in {(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}";
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
        var firstDay = _selectedHistoryWeekStart;
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

        var lastDay = firstDay.AddDays(6);
        SelectedWeekText.Text = firstDay.Year == lastDay.Year
            ? $"{firstDay:MMM d} – {lastDay:MMM d, yyyy}"
            : $"{firstDay:MMM d, yyyy} – {lastDay:MMM d, yyyy}";
        NextWeekButton.IsEnabled = _selectedHistoryWeekStart < StartOfWeek(DateTime.Today);
    }

    private void RefreshRecentActivity()
    {
        var recentItems = _earnings
            .Where(record => record.TimestampUtc.ToLocalTime().Date == _selectedHistoryDate.Date)
            .OrderByDescending(record => record.TimestampUtc)
            .Select(record => new RecentEarningItem(
                record.Id,
                string.IsNullOrWhiteSpace(record.Source) ? "Earning" : record.Source,
                FormatMoney(record.Amount),
                FormatLocalTimestamp(record.TimestampUtc)))
            .ToList();

        RecentActivityItems.ItemsSource = recentItems;
        SelectedHistoryDateText.Text = _selectedHistoryDate == DateTime.Today
            ? "Today"
            : _selectedHistoryDate.ToString("dddd, MMMM d", CultureInfo.CurrentCulture);
        HistoryDayTotalText.Text = FormatMoney(recentItems.Count == 0
            ? 0
            : _earnings
                .Where(record => record.TimestampUtc.ToLocalTime().Date == _selectedHistoryDate.Date)
                .Sum(record => record.Amount));
        NoRecentActivityText.Visibility = recentItems.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static DateTime StartOfWeek(DateTime date)
    {
        var difference = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
        return date.Date.AddDays(-difference);
    }

    private void PreviousWeekButton_Click(object sender, RoutedEventArgs e)
    {
        _selectedHistoryWeekStart = _selectedHistoryWeekStart.AddDays(-7);
        RefreshWeeklyChart();
    }

    private void NextWeekButton_Click(object sender, RoutedEventArgs e)
    {
        var currentWeek = StartOfWeek(DateTime.Today);
        if (_selectedHistoryWeekStart < currentWeek)
        {
            _selectedHistoryWeekStart = _selectedHistoryWeekStart.AddDays(7);
            RefreshWeeklyChart();
        }
    }

    private void RefreshCalendar()
    {
        var firstOfMonth = new DateTime(
            _displayedHistoryMonth.Year,
            _displayedHistoryMonth.Month,
            1);
        var firstVisibleDay = firstOfMonth.AddDays(-(int)firstOfMonth.DayOfWeek);
        var earningDates = _earnings
            .Select(record => record.TimestampUtc.ToLocalTime().Date)
            .ToHashSet();

        CalendarMonthText.Text = firstOfMonth.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
        CalendarDayItems.ItemsSource = Enumerable.Range(0, 42)
            .Select(offset =>
            {
                var date = firstVisibleDay.AddDays(offset);
                return new CalendarDayItem(
                    date,
                    date.Day.ToString(CultureInfo.CurrentCulture),
                    date.Month == firstOfMonth.Month,
                    date == DateTime.Today,
                    date == _selectedHistoryDate,
                    earningDates.Contains(date),
                    date <= DateTime.Today);
            })
            .ToList();
        NextCalendarMonthButton.IsEnabled = firstOfMonth <
            new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
    }

    private void PreviousCalendarMonthButton_Click(object sender, RoutedEventArgs e)
    {
        _displayedHistoryMonth = _displayedHistoryMonth.AddMonths(-1);
        RefreshCalendar();
    }

    private void NextCalendarMonthButton_Click(object sender, RoutedEventArgs e)
    {
        var currentMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        if (_displayedHistoryMonth < currentMonth)
        {
            _displayedHistoryMonth = _displayedHistoryMonth.AddMonths(1);
            RefreshCalendar();
        }
    }

    private void CalendarDayButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: DateTime selectedDate } ||
            selectedDate.Date > DateTime.Today)
        {
            return;
        }

        _selectedHistoryDate = selectedDate.Date;
        _displayedHistoryMonth = new DateTime(selectedDate.Year, selectedDate.Month, 1);
        RefreshCalendar();
        RefreshRecentActivity();
    }

    private void PreviousHistoryDayButton_Click(object sender, RoutedEventArgs e)
    {
        _selectedHistoryDate = _selectedHistoryDate.AddDays(-1);
        _displayedHistoryMonth = new DateTime(
            _selectedHistoryDate.Year,
            _selectedHistoryDate.Month,
            1);
        RefreshCalendar();
        RefreshRecentActivity();
    }

    private void NextHistoryDayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedHistoryDate < DateTime.Today)
        {
            _selectedHistoryDate = _selectedHistoryDate.AddDays(1);
            _displayedHistoryMonth = new DateTime(
                _selectedHistoryDate.Year,
                _selectedHistoryDate.Month,
                1);
            RefreshCalendar();
            RefreshRecentActivity();
        }
    }

    private async void DeleteRecentEarningButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: Guid recordId })
        {
            return;
        }

        var record = _earnings.FirstOrDefault(item => item.Id == recordId);
        if (record is null)
        {
            return;
        }

        var response = System.Windows.MessageBox.Show(
            $"Delete {FormatMoney(record.Amount)} from {FormatLocalTimestamp(record.TimestampUtc)}?",
            "Delete earning",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (response != MessageBoxResult.Yes)
        {
            return;
        }

        await UndoEarningRecordAsync(
            record,
            "That earning could not be deleted because the local data file could not be saved.");
    }

    private void UpdateDashboardStatus()
    {
        var hasCaptureArea = GetAutomaticCaptureArea() is not null;
        var ocrReady = _ocrService.IsInitialized;
        var quickScanStatus = _pendingDetectedPayout is not null
            ? "Verify payout"
            : hasCaptureArea
                ? "Quick Scan ready"
                : "Display unavailable";
        const bool automaticDetectionEnabled = false;

        LiveScannerStatusText.Text = quickScanStatus;
        LiveScannerDot.Fill = GetThemeBrush(hasCaptureArea ? "EarningsGreenBrush" : "WarningGoldBrush");
        CompactScannerStatusText.Text = quickScanStatus;
        CompactLiveScannerDot.Fill = GetThemeBrush(hasCaptureArea ? "EarningsGreenBrush" : "WarningGoldBrush");

        OcrReadyStatusText.Text = ocrReady ? "Ready" : "Standby";
        OcrReadyDot.Fill = GetThemeBrush(ocrReady ? "ScannerCyanBrush" : "MutedTextBrush");

        CaptureAreaStatusText.Text = hasCaptureArea ? "Full screen" : "Unavailable";
        CaptureAreaDot.Fill = GetThemeBrush(hasCaptureArea ? "EarningsGreenBrush" : "WarningGoldBrush");
        CompactCaptureReadyDot.Fill = GetThemeBrush(hasCaptureArea ? "EarningsGreenBrush" : "WarningGoldBrush");
        CompactCaptureReadyText.Text = hasCaptureArea ? "FULL SCREEN" : "DISPLAY UNAVAILABLE";
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
        UpdatePageNavigation();

        if (saveSelection)
        {
            _captureSettings.LastSelectedTab = _selectedTab;
        }
    }

    private async Task SelectTabAnimatedAsync(string tabName)
    {
        _requestedTab = tabName.Equals("Settings", StringComparison.OrdinalIgnoreCase)
            ? "Settings"
            : "Dashboard";
        if (_isTabTransitioning || _isModeTransitioning) return;
        _isTabTransitioning = true;
        try
        {
            while (_selectedTab != _requestedTab && !_isClosing)
            {
                var targetTab = _requestedTab;
                var movingToSettings = targetTab == "Settings";
                var outgoing = movingToSettings ? MainScrollViewer : SettingsScrollViewer;
                var incoming = movingToSettings ? SettingsScrollViewer : MainScrollViewer;
                var direction = movingToSettings ? 1d : -1d;
                var outgoingTransform = (TranslateTransform)outgoing.RenderTransform;
                var incomingTransform = (TranslateTransform)incoming.RenderTransform;

                incoming.Visibility = Visibility.Visible;
                incoming.ScrollToTop();
                // Both pages travel the same distance together, like a single horizontal strip.
                var distance = Math.Max(1, ExpandedDashboard.ActualWidth);
                incomingTransform.X = distance * direction;
                ExpandedDashboard.IsHitTestVisible = false;
                UpdatePageNavigation();
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };
                var duration = TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 360 : 1);
                var slide = new DoubleAnimation(distance * direction, 0, duration) { EasingFunction = easing };
                slide.Completed += (_, _) => completion.TrySetResult();
                outgoingTransform.BeginAnimation(
                    TranslateTransform.XProperty,
                    new DoubleAnimation(0, -distance * direction, duration) { EasingFunction = easing });
                incomingTransform.BeginAnimation(TranslateTransform.XProperty, slide);
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
                UpdatePageNavigation();
            }
        }
        finally
        {
            ExpandedDashboard.IsHitTestVisible = true;
            _isTabTransitioning = false;
        }
    }

    private void UpdatePageNavigation()
    {
        SettingsBackButton.Visibility = _selectedTab == "Settings" ? Visibility.Visible : Visibility.Collapsed;
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
            QuickScanHotkeyHintText.Text = _captureSettings.HotkeyEnabled
                ? $"Shortcut: {CurrentHotkeyText.Text}"
                : "Shortcut disabled in Settings";
            _proposedHotkeyModifiers = (GlobalHotkeyModifiers)_captureSettings.HotkeyModifiers;
            _proposedHotkeyVirtualKey = (uint)_captureSettings.HotkeyVirtualKey;
            _proposedHotkeyText = CurrentHotkeyText.Text;
            ProposedHotkeyText.Text = string.Empty;
            MinimizeHotkeyEnabledCheckBox.IsChecked = _captureSettings.MinimizeHotkeyEnabled;
            CurrentMinimizeHotkeyText.Text = string.IsNullOrWhiteSpace(_captureSettings.MinimizeHotkeyDisplayText)
                ? "Ctrl + Shift + M"
                : _captureSettings.MinimizeHotkeyDisplayText;
            _proposedMinimizeHotkeyModifiers =
                (GlobalHotkeyModifiers)_captureSettings.MinimizeHotkeyModifiers;
            _proposedMinimizeHotkeyVirtualKey =
                (uint)_captureSettings.MinimizeHotkeyVirtualKey;
            _proposedMinimizeHotkeyText = CurrentMinimizeHotkeyText.Text;
            ProposedMinimizeHotkeyText.Text = string.Empty;
            AlwaysOnTopSettingCheckBox.IsChecked = _captureSettings.AlwaysOnTop;
            RememberWindowPositionCheckBox.IsChecked = _captureSettings.RememberWindowPosition;
            StartCompactSettingCheckBox.IsChecked = _captureSettings.IsCompactMode;
            PauseLivePreviewWhenHiddenCheckBox.IsChecked = _captureSettings.PauseLivePreviewWhenHidden;
            ContinueScannerWhileHiddenCheckBox.IsChecked = _captureSettings.ContinueScannerWhileHidden;
            ShowTrayNotificationCheckBox.IsChecked = _captureSettings.ShowTrayNotificationAfterPayout;
            ShowScannerPerformanceDiagnosticsCheckBox.IsChecked = _captureSettings.ShowScannerPerformanceDiagnostics;
            PaddleOcrEnabledCheckBox.IsChecked = _captureSettings.PaddleOcrEnabled;
            UpdatePaddleOcrStatus();
            ScannerDetectionModeComboBox.SelectedIndex = (int)_captureSettings.DetectionMode;
            ScannerAreaModeComboBox.SelectedIndex = (int)ScannerAreaMode.SelectedRegion;
            RequireQuickConfirmationCheckBox.IsChecked = true;
            ThemeComboBox.SelectedItem = ThemeComboBox.Items
                .OfType<System.Windows.Controls.ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag?.ToString(),
                    _uiPreferences.Theme,
                    StringComparison.OrdinalIgnoreCase)) ?? ThemeComboBox.Items[0];
            ProfileUsernameTextBox.Text = _discordSettings.Username;
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
        if (_globalHotkeyService is null || _minimizeHotkeyService is null)
        {
            HotkeyRegistrationStatusText.Text = "Registration status: Window handle unavailable";
            MinimizeHotkeyRegistrationStatusText.Text = "Registration status: Window handle unavailable";
            return;
        }

        if (!_captureSettings.HotkeyEnabled)
        {
            _globalHotkeyService.Disable();
            HotkeyRegistrationStatusText.Text = "Registration status: Disabled";
        }
        else if (_globalHotkeyService.TryRegister(
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

        if (!_captureSettings.MinimizeHotkeyEnabled)
        {
            _minimizeHotkeyService.Disable();
            MinimizeHotkeyRegistrationStatusText.Text = "Registration status: Disabled";
        }
        else if (_minimizeHotkeyService.TryRegister(
                     (GlobalHotkeyModifiers)_captureSettings.MinimizeHotkeyModifiers,
                     (uint)_captureSettings.MinimizeHotkeyVirtualKey,
                     out _))
        {
            MinimizeHotkeyRegistrationStatusText.Text =
                $"Registration status: Active ({_captureSettings.MinimizeHotkeyDisplayText})";
        }
        else
        {
            MinimizeHotkeyRegistrationStatusText.Text =
                "That shortcut is already being used by another application.";
        }
    }

    private async void GlobalHotkeyService_HotkeyPressed(object? sender, EventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        if (!HasUsableScreen())
        {
            ShowExistingMainWindow();
            ShowCaptureSetupState();
            ShowQuickCaptureStatusNotification("Primary display unavailable.");
            return;
        }

        if (_pendingDetectedPayout is not null)
        {
            ShowExistingMainWindow();
            return;
        }

        var shouldShowResult = !IsVisible || WindowState == WindowState.Minimized;
        await RunQuickCaptureAsync(_shutdownCancellation.Token, ScanRequestMode.Normal);
        if (shouldShowResult && !_isClosing)
        {
            ShowExistingMainWindow();
        }
    }

    private void MinimizeHotkeyService_HotkeyPressed(object? sender, EventArgs e)
    {
        if (!_isClosing && IsVisible)
        {
            WindowState = WindowState.Minimized;
        }
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
            HasUsableScreen())
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
        _hotkeyRecordingTarget = HotkeyRecordingTarget.QuickScan;
        _isRecordingHotkey = true;
        ProposedHotkeyText.Text = "Press your new shortcut…";
        HotkeyWarningText.Visibility = Visibility.Collapsed;
        HotkeyRegistrationStatusText.Text = "Press your new shortcut…";
        RecordHotkeyButton.Focus();
    }

    private void RecordMinimizeHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _hotkeyRecordingTarget = HotkeyRecordingTarget.Minimize;
        _isRecordingHotkey = true;
        ProposedMinimizeHotkeyText.Text = "Press your new shortcut…";
        MinimizeHotkeyWarningText.Visibility = Visibility.Collapsed;
        MinimizeHotkeyRegistrationStatusText.Text = "Press your new shortcut…";
        RecordMinimizeHotkeyButton.Focus();
    }

    private void RecordProposedHotkey(KeyEventArgs e)
    {
        if (_hotkeyRecordingTarget == HotkeyRecordingTarget.Minimize)
        {
            RecordProposedMinimizeHotkey(e);
            return;
        }

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

    private void RecordProposedMinimizeHotkey(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            _isRecordingHotkey = false;
            ProposedMinimizeHotkeyText.Text = string.Empty;
            MinimizeHotkeyRegistrationStatusText.Text = "Shortcut recording cancelled.";
            return;
        }

        if (IsModifierKey(key))
        {
            ProposedMinimizeHotkeyText.Text = "Press a non-modifier key…";
            return;
        }

        if (key is Key.Enter or Key.Tab or Key.F12 || key == Key.None)
        {
            MinimizeHotkeyRegistrationStatusText.Text =
                "That key cannot be used as a global shortcut.";
            return;
        }

        var modifiers = ConvertModifiers(Keyboard.Modifiers);
        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0)
        {
            MinimizeHotkeyRegistrationStatusText.Text =
                "That key could not be converted to a Windows shortcut.";
            return;
        }

        _proposedMinimizeHotkeyModifiers = modifiers;
        _proposedMinimizeHotkeyVirtualKey = virtualKey;
        _proposedMinimizeHotkeyText = FormatHotkey(modifiers, key);
        _isRecordingHotkey = false;
        ProposedMinimizeHotkeyText.Text = $"Proposed: {_proposedMinimizeHotkeyText}";
        MinimizeHotkeyRegistrationStatusText.Text =
            "Shortcut recorded. Select Apply Minimize Shortcut to register it.";
        ShowMinimizeKeyWarning(modifiers == GlobalHotkeyModifiers.None);
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

    private void ResetMinimizeHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _isRecordingHotkey = false;
        _proposedMinimizeHotkeyModifiers =
            GlobalHotkeyModifiers.Control | GlobalHotkeyModifiers.Shift;
        _proposedMinimizeHotkeyVirtualKey = 0x4D;
        _proposedMinimizeHotkeyText = "Ctrl + Shift + M";
        ProposedMinimizeHotkeyText.Text = "Proposed: Ctrl + Shift + M";
        MinimizeHotkeyRegistrationStatusText.Text =
            "Default selected. Select Apply Minimize Shortcut to register it.";
        ShowMinimizeKeyWarning(show: false);
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
            QuickScanHotkeyHintText.Text = "Shortcut disabled in Settings";
            HotkeyRegistrationStatusText.Text = "Registration status: Disabled";
            await SaveSettingsQuietlyAsync();
            return;
        }

        if (_captureSettings.MinimizeHotkeyEnabled &&
            HotkeysMatch(
                _proposedHotkeyModifiers,
                _proposedHotkeyVirtualKey,
                (GlobalHotkeyModifiers)_captureSettings.MinimizeHotkeyModifiers,
                (uint)_captureSettings.MinimizeHotkeyVirtualKey))
        {
            HotkeyRegistrationStatusText.Text =
                "Quick Scan and minimize must use different shortcuts.";
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
        QuickScanHotkeyHintText.Text = $"Shortcut: {_proposedHotkeyText}";
        ProposedHotkeyText.Text = string.Empty;
        HotkeyRegistrationStatusText.Text = $"Registration status: Active ({_proposedHotkeyText})";
        await SaveSettingsQuietlyAsync();
    }

    private async void ApplyMinimizeHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_minimizeHotkeyService is null)
        {
            MinimizeHotkeyRegistrationStatusText.Text =
                "The window is not ready to register a shortcut.";
            return;
        }

        if (MinimizeHotkeyEnabledCheckBox.IsChecked != true)
        {
            _minimizeHotkeyService.Disable();
            _captureSettings.MinimizeHotkeyEnabled = false;
            MinimizeHotkeyRegistrationStatusText.Text = "Registration status: Disabled";
            await SaveSettingsQuietlyAsync();
            return;
        }

        if (_captureSettings.HotkeyEnabled &&
            HotkeysMatch(
                _proposedMinimizeHotkeyModifiers,
                _proposedMinimizeHotkeyVirtualKey,
                (GlobalHotkeyModifiers)_captureSettings.HotkeyModifiers,
                (uint)_captureSettings.HotkeyVirtualKey))
        {
            MinimizeHotkeyRegistrationStatusText.Text =
                "Quick Scan and minimize must use different shortcuts.";
            return;
        }

        if (!_minimizeHotkeyService.TryRegister(
                _proposedMinimizeHotkeyModifiers,
                _proposedMinimizeHotkeyVirtualKey,
                out _))
        {
            MinimizeHotkeyRegistrationStatusText.Text =
                "That shortcut is already being used by another application.";
            return;
        }

        _captureSettings.MinimizeHotkeyEnabled = true;
        _captureSettings.MinimizeHotkeyModifiers = (int)_proposedMinimizeHotkeyModifiers;
        _captureSettings.MinimizeHotkeyVirtualKey = (int)_proposedMinimizeHotkeyVirtualKey;
        _captureSettings.MinimizeHotkeyDisplayText = _proposedMinimizeHotkeyText;
        CurrentMinimizeHotkeyText.Text = _proposedMinimizeHotkeyText;
        ProposedMinimizeHotkeyText.Text = string.Empty;
        MinimizeHotkeyRegistrationStatusText.Text =
            $"Registration status: Active ({_proposedMinimizeHotkeyText})";
        await SaveSettingsQuietlyAsync();
    }

    private static bool HotkeysMatch(
        GlobalHotkeyModifiers firstModifiers,
        uint firstKey,
        GlobalHotkeyModifiers secondModifiers,
        uint secondKey) => firstModifiers == secondModifiers && firstKey == secondKey;

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

    private async void PaddleOcrEnabledCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsUiEvents)
        {
            return;
        }

        _captureSettings.PaddleOcrEnabled = PaddleOcrEnabledCheckBox.IsChecked == true;
        _paddleOcrService.Enabled = _captureSettings.PaddleOcrEnabled;
        PaddleOcrModelStatusText.Text = _paddleOcrService.Enabled
            ? "AI model status: Loading local model…"
            : "AI model status: Disabled";
        await SaveSettingsQuietlyAsync();
        await InitializePaddleOcrStatusAsync();
    }

    private async Task InitializePaddleOcrStatusAsync()
    {
        try
        {
            var available = false;
            if (_paddleOcrService.Enabled)
            {
                available = await Task.Run(
                    () => _paddleOcrService.IsAvailable,
                    _shutdownCancellation.Token);
            }

            if (!_isClosing)
            {
                UpdatePaddleOcrStatus();
                if (_paddleOcrService.Enabled && !available)
                {
                    OcrStatusText.Text = PaddleOcrRecognitionService.UnavailableMessage;
                }
            }
        }
        catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
        {
        }
    }

    private void UpdatePaddleOcrStatus()
    {
        if (!_paddleOcrService.Enabled)
        {
            PaddleOcrModelStatusText.Text = "AI model status: Disabled";
        }
        else if (_paddleOcrService.ModelLoadDuration is null)
        {
            PaddleOcrModelStatusText.Text = "AI model status: Loading local model…";
        }
        else
        {
            PaddleOcrModelStatusText.Text = $"AI model status: {_paddleOcrService.ModelStatus}";
        }

        PaddleOcrLoadTimeText.Text = $"AI model load time: {(_paddleOcrService.ModelLoadDuration is TimeSpan load ? $"{load.TotalMilliseconds:0} ms" : "--")}";
        PaddleOcrLatestTimeText.Text = $"Latest PaddleOCR processing time: {(_paddleOcrService.LatestProcessingDuration is TimeSpan latest ? $"{latest.TotalMilliseconds:0.0} ms" : "--")}";
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

    private async void SaveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var username = ProfileUsernameTextBox.Text.Trim();
        if (username.Contains('\r') || username.Contains('\n'))
        {
            ProfileStatusText.Foreground = GetThemeBrush("ErrorRedBrush");
            ProfileStatusText.Text = "Player name must be a single line.";
            return;
        }

        try
        {
            _discordSettings.Username = username;
            await _discordSettingsService.SaveAsync(_discordSettings);
            RefreshTotals();
            ProfileStatusText.Foreground = GetThemeBrush("EarningsGreenBrush");
            ProfileStatusText.Text = string.IsNullOrWhiteSpace(username)
                ? "Saved. Greetings will use Player."
                : $"Saved. Welcome, {username}!";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ProfileStatusText.Foreground = GetThemeBrush("ErrorRedBrush");
            ProfileStatusText.Text = "The player name could not be saved locally.";
        }
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
        var username = ProfileUsernameTextBox.Text.Trim();
        var webhookUrl = DiscordWebhookPasswordBox.Password.Trim();
        var enabled = DiscordEnabledCheckBox.IsChecked == true;
        var isEmptyDisabledConfiguration = !requireConfigured &&
            !enabled &&
            string.IsNullOrWhiteSpace(webhookUrl);

        if (!isEmptyDisabledConfiguration)
        {
            if (string.IsNullOrWhiteSpace(username))
            {
                SetDiscordStatus("Add a username before configuring the Discord webhook.", false);
                ProfileUsernameTextBox.Focus();
                return false;
            }

            if (username.Contains('\r') || username.Contains('\n'))
            {
                SetDiscordStatus("The Discord username must be a single line.", false);
                ProfileUsernameTextBox.Focus();
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
        ProfileUsernameTextBox.IsEnabled = enabled;
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
        UpdatePresentationActivity();
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

    private void ShowMinimizeKeyWarning(bool show)
    {
        MinimizeHotkeyWarningText.Visibility = show
            ? Visibility.Visible
            : Visibility.Collapsed;
        MinimizeHotkeyWarningText.Text = show
            ? "A shortcut containing Ctrl, Alt, or Shift is recommended."
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
        if (_isModeTransitioning || _isTabTransitioning || _isCompactMode == isCompactMode)
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
        try
        {
            await AnimateWindowSizeAsync(targetWidth, targetHeight, isCompactMode);
            if (_isClosing) return;
            StartAmbientAnimations();
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

    private void ApplyWindowMode(bool isCompactMode, bool resize = true)
    {
        _isCompactMode = isCompactMode;
        ExpandedDashboard.Visibility = isCompactMode ? Visibility.Collapsed : Visibility.Visible;
        CompactDashboard.Visibility = isCompactMode ? Visibility.Visible : Visibility.Collapsed;

        CompactModeTitleButton.Visibility = isCompactMode ? Visibility.Collapsed : Visibility.Visible;
        ExpandedModeTitleButton.Visibility = isCompactMode ? Visibility.Visible : Visibility.Collapsed;
        ExpandedTitleSubtitle.Visibility = isCompactMode ? Visibility.Collapsed : Visibility.Visible;
        CompactTitleScannerPanel.Visibility = isCompactMode ? Visibility.Visible : Visibility.Collapsed;

        if (!resize) return;

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

    private async Task AnimateWindowSizeAsync(double width, double height, bool? targetCompactMode = null)
    {
        var version = ++_resizeVersion;
        _resizeCompletion?.TrySetResult();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _resizeCompletion = completion;
        // Crossfade the old snapshot DURING the resize. Keep the incoming layout
        // at its final size and animate only its render transform, avoiding a
        // second reveal animation and repeated layout work on every frame.
        if (ModeTransitionPreview.Source is null && WindowChrome.IsVisible)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var snapshot = new RenderTargetBitmap(
                Math.Max(1, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX)),
                Math.Max(1, (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY)),
                96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            snapshot.Render(WindowHost);
            snapshot.Freeze();
            ModeTransitionPreview.Source = snapshot;
            ModeTransitionPreview.Visibility = Visibility.Visible;
            ModeTransitionPreview.Opacity = 1;
        }
        StopAmbientAnimations();
        var startingWidth = ActualWidth;
        var startingHeight = ActualHeight;
        if (targetCompactMode.HasValue) ApplyWindowMode(targetCompactMode.Value, resize: false);
        WindowChrome.Width = Math.Max(1, width - 16);
        WindowChrome.Height = Math.Max(1, height - 16);
        WindowChrome.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        WindowChrome.VerticalAlignment = VerticalAlignment.Top;
        WindowChrome.Visibility = Visibility.Visible;
        WindowChrome.Opacity = 1;
        WindowHost.IsHitTestVisible = false;
        var scale = new ScaleTransform(
            Math.Max(1, startingWidth - 16) / WindowChrome.Width,
            Math.Max(1, startingHeight - 16) / WindowChrome.Height);
        WindowChrome.RenderTransform = scale;
        WindowChrome.CacheMode = new BitmapCache();
        MinWidth = 0;
        MinHeight = 0;
        MaxWidth = double.PositiveInfinity;
        var duration = SystemParameters.ClientAreaAnimation ? 360 : 1;
        var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var widthAnimation = new DoubleAnimation(startingWidth, width, TimeSpan.FromMilliseconds(duration))
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };
        var heightAnimation = new DoubleAnimation(startingHeight, height, TimeSpan.FromMilliseconds(duration))
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };
        heightAnimation.Completed += (_, _) => completion.TrySetResult();
        Width = width;
        Height = height;
        BeginAnimation(WidthProperty, widthAnimation);
        BeginAnimation(HeightProperty, heightAnimation);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(scale.ScaleX, 1, TimeSpan.FromMilliseconds(duration)) { EasingFunction = easing });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(scale.ScaleY, 1, TimeSpan.FromMilliseconds(duration)) { EasingFunction = easing });
        ModeTransitionPreview.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(duration)) { EasingFunction = easing });
        await completion.Task;
        if (version != _resizeVersion || _isClosing) return;
        BeginAnimation(WidthProperty, null);
        BeginAnimation(HeightProperty, null);
        Width = width;
        Height = height;
        if (_isCompactMode)
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
        WindowChrome.RenderTransform = Transform.Identity;
        WindowChrome.CacheMode = null;
        WindowChrome.Width = double.NaN;
        WindowChrome.Height = double.NaN;
        WindowChrome.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        WindowChrome.VerticalAlignment = VerticalAlignment.Stretch;
        ModeTransitionPreview.BeginAnimation(OpacityProperty, null);
        ModeTransitionPreview.Visibility = Visibility.Collapsed;
        ModeTransitionPreview.Source = null;
        WindowHost.IsHitTestVisible = true;
    }

    private void WindowContent_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        WindowContentClip.Rect = new Rect(0, 0, Math.Max(0, e.NewSize.Width), Math.Max(0, e.NewSize.Height));
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
            PayoutDetectionMethod.GreenCashIconPayout => "Green cash icon payout",
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
