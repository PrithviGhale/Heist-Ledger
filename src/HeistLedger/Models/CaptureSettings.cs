using System.Text.Json.Serialization;

namespace GTAEarningsTracker.Models;

public enum ScannerDetectionMode
{
    Fast,
    Balanced,
    Thorough
}

public enum ScannerAreaMode
{
    AutoLocate,
    SelectedRegion
}

public sealed class CaptureSettings
{
    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public bool CaptureAreaConfirmed { get; set; }

    public bool IsCompactMode { get; set; }

    public bool HotkeyEnabled { get; set; } = true;

    public int HotkeyModifiers { get; set; } = 0x0002 | 0x0004;

    public int HotkeyVirtualKey { get; set; } = 0x48;

    public string HotkeyDisplayText { get; set; } = "Ctrl + Shift + H";

    public bool MinimizeHotkeyEnabled { get; set; } = true;

    public int MinimizeHotkeyModifiers { get; set; } = 0x0002 | 0x0004;

    public int MinimizeHotkeyVirtualKey { get; set; } = 0x4D;

    public string MinimizeHotkeyDisplayText { get; set; } = "Ctrl + Shift + M";

    public bool AlwaysOnTop { get; set; } = true;

    public bool RememberWindowPosition { get; set; } = true;

    public double? WindowLeft { get; set; }

    public double? WindowTop { get; set; }

    public bool PauseLivePreviewWhenHidden { get; set; } = true;

    public bool ContinueScannerWhileHidden { get; set; } = true;

    public bool ShowTrayNotificationAfterPayout { get; set; } = true;

    public bool ShowScannerPerformanceDiagnostics { get; set; }

    public bool PaddleOcrEnabled { get; set; } = true;

    public ScannerDetectionMode DetectionMode { get; set; } = ScannerDetectionMode.Fast;

    public ScannerAreaMode AreaMode { get; set; } = ScannerAreaMode.SelectedRegion;

    public string QuickCaptureMonitorId { get; set; } = "Primary";

    public bool RequireQuickCaptureConfirmation { get; set; } = true;

    public string LastSelectedTab { get; set; } = "Dashboard";

    public bool CheckForUpdatesOnStartup { get; set; } = true;

    [JsonIgnore]
    public bool HasCaptureArea => Width > 0 && Height > 0;

    [JsonIgnore]
    public bool HasConfirmedCaptureArea => CaptureAreaConfirmed && HasCaptureArea;
}
