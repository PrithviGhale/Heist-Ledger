using System.Windows.Media.Imaging;
using GTAEarningsTracker.Models;

namespace GTAEarningsTracker.Services;

public enum ScreenCaptureBackend
{
    WindowsGraphicsCapture,
    GdiFallback
}

public sealed class ContinuousScreenCaptureService : IDisposable
{
    private readonly ScreenCaptureService _fallbackCapture;
    private readonly Func<IReadOnlyList<System.Windows.Int32Rect>>? _excludedRegionsProvider;
    private CaptureSettings? _settings;
    private bool _disposed;

    public ContinuousScreenCaptureService(
        ScreenCaptureService fallbackCapture,
        Func<IReadOnlyList<System.Windows.Int32Rect>>? excludedRegionsProvider = null)
    {
        _fallbackCapture = fallbackCapture;
        _excludedRegionsProvider = excludedRegionsProvider;
    }

    public ScreenCaptureBackend Backend { get; private set; } = ScreenCaptureBackend.GdiFallback;

    public string BackendDisplayName => Backend == ScreenCaptureBackend.WindowsGraphicsCapture
        ? "Windows Graphics Capture"
        : "GDI fallback";

    public string FallbackReason { get; private set; } = string.Empty;

    public bool IsRunning => _settings is not null && !_disposed;

    public void Start(CaptureSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_fallbackCapture.IsValidCaptureArea(settings))
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "The selected capture rectangle is invalid.");
        }

        _settings = CloneArea(settings);

        // This build intentionally avoids pretending that GDI is modern capture. The local
        // Windows SDK does not expose the WinRT projection needed to create and consume a
        // Direct3D11CaptureFramePool without adding another package, so the safe fallback is
        // selected and surfaced in diagnostics.
        Backend = ScreenCaptureBackend.GdiFallback;
        FallbackReason =
            "Windows.Graphics.Capture projection is unavailable in this build; using the safe GDI capture fallback.";
    }

    public BitmapSource CaptureFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var settings = _settings ?? throw new InvalidOperationException("The continuous capture session is not running.");
        return _fallbackCapture.Capture(settings, _excludedRegionsProvider?.Invoke());
    }

    public void Stop()
    {
        _settings = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settings = null;
    }

    private static CaptureSettings CloneArea(CaptureSettings settings)
    {
        return new CaptureSettings
        {
            X = settings.X,
            Y = settings.Y,
            Width = settings.Width,
            Height = settings.Height
        };
    }
}
