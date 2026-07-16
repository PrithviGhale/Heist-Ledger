using System.Threading.Channels;
using System.Windows.Media.Imaging;
using GTAEarningsTracker.Models;

namespace GTAEarningsTracker.Services;

public enum BackgroundScannerState
{
    Disabled,
    Starting,
    Watching,
    ChangeDetected,
    Stabilizing,
    ReadingHeading,
    ReadingAmount,
    FallbackAnalysis,
    PayoutDetected,
    WaitingForScreenToDisappear,
    Paused,
    Error
}

public sealed record BackgroundScannerSnapshot(
    BackgroundScannerState State,
    string CaptureBackend,
    string Message,
    double FrameChangePercentage,
    TimeSpan StableFor,
    long FramesReceived,
    long FramesIgnoredAsUnchanged,
    long OcrScansPerformed,
    DateTime? LastSuccessfulDetectionUtc);

public sealed class ScannerFrameEventArgs(BitmapSource frame) : EventArgs
{
    public BitmapSource Frame { get; } = frame;
}

public sealed class ScannerAnalysisEventArgs(
    PayoutAnalysisResult analysis,
    BackgroundScannerSnapshot snapshot) : EventArgs
{
    public PayoutAnalysisResult Analysis { get; } = analysis;

    public BackgroundScannerSnapshot Snapshot { get; } = snapshot;
}

public sealed class ScannerSnapshotEventArgs(BackgroundScannerSnapshot snapshot) : EventArgs
{
    public BackgroundScannerSnapshot Snapshot { get; } = snapshot;
}

public sealed class BackgroundScannerService : IAsyncDisposable
{
    public static readonly TimeSpan CaptureCadence = TimeSpan.FromMilliseconds(120);
    public const int LatestFrameQueueCapacity = 1;

    private readonly ContinuousScreenCaptureService _capture;
    private readonly PayoutDetectionService _detector;
    private readonly GtaLargeDigitReaderService? _burstReader;
    private readonly FrameChangeDetector _changeDetector = new();
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    private Channel<BitmapSource>? _frames;
    private CancellationTokenSource? _sessionCancellation;
    private Task? _captureTask;
    private Task? _processingTask;
    private ScannerDetectionMode _mode;
    private long _framesReceived;
    private long _framesIgnored;
    private long _ocrScans;
    private double _lastChangePercentage;
    private TimeSpan _lastStableFor;
    private DateTime? _lastSuccessfulDetectionUtc;
    private PayoutAnalysisResult? _pendingDisappearanceAnalysis;
    private int _disappearanceFrames;

    public BackgroundScannerService(
        ContinuousScreenCaptureService capture,
        PayoutDetectionService detector,
        GtaLargeDigitReaderService? burstReader = null)
    {
        _capture = capture;
        _detector = detector;
        _burstReader = burstReader;
        Snapshot = CreateSnapshot(BackgroundScannerState.Disabled, "Scanner disabled.");
    }

    public event EventHandler<ScannerFrameEventArgs>? FrameAvailable;

    public event EventHandler<ScannerAnalysisEventArgs>? AnalysisCompleted;

    public event EventHandler<ScannerSnapshotEventArgs>? SnapshotChanged;

    public BackgroundScannerSnapshot Snapshot { get; private set; }

    public bool IsRunning => _sessionCancellation is { IsCancellationRequested: false };

    public async Task StartAsync(
        CaptureSettings settings,
        ScannerDetectionMode mode,
        CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync();
            PublishState(BackgroundScannerState.Starting, "Starting capture session.");
            _mode = mode;
            _framesReceived = 0;
            _framesIgnored = 0;
            _ocrScans = 0;
            _lastChangePercentage = 0;
            _lastStableFor = TimeSpan.Zero;
            _pendingDisappearanceAnalysis = null;
            _disappearanceFrames = 0;
            _changeDetector.Reset();
            _capture.Start(settings);
            _frames = Channel.CreateBounded<BitmapSource>(new BoundedChannelOptions(LatestFrameQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            });
            _sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _sessionCancellation.Token;
            _captureTask = Task.Run(() => CaptureLoopAsync(token), token);
            _processingTask = Task.Run(() => ProcessLoopAsync(token), token);
            PublishState(
                BackgroundScannerState.Watching,
                _capture.Backend == ScreenCaptureBackend.GdiFallback
                    ? _capture.FallbackReason
                    : "Watching the selected capture region.");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public void SetPaused(bool paused)
    {
        if (!IsRunning)
        {
            return;
        }

        PublishState(
            paused ? BackgroundScannerState.Paused : BackgroundScannerState.Watching,
            paused ? "Scanner paused while the window is hidden." : "Scanner resumed.");
    }

    private async Task CaptureLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(CaptureCadence);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (Snapshot.State != BackgroundScannerState.Paused)
                {
                    var frame = _capture.CaptureFrame();
                    Interlocked.Increment(ref _framesReceived);
                    _frames?.Writer.TryWrite(frame);
                    FrameAvailable?.Invoke(this, new ScannerFrameEventArgs(frame));
                }

                await timer.WaitForNextTickAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            PublishState(BackgroundScannerState.Error, $"Capture failed: {ex.Message}");
            _frames?.Writer.TryComplete(ex);
        }
        finally
        {
            _frames?.Writer.TryComplete();
        }
    }

    private async Task ProcessLoopAsync(CancellationToken cancellationToken)
    {
        var reader = _frames?.Reader ?? throw new InvalidOperationException("The frame queue was not initialized.");
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken))
            {
                if (!reader.TryRead(out var frame))
                {
                    continue;
                }

                while (reader.TryRead(out var newerFrame))
                {
                    frame = newerFrame;
                    Interlocked.Increment(ref _framesIgnored);
                }

                if (Snapshot.State == BackgroundScannerState.Paused)
                {
                    continue;
                }

                var observation = _changeDetector.Observe(frame, DateTime.UtcNow);
                _lastChangePercentage = observation.ChangePercentage;
                _lastStableFor = observation.StableFor;
                switch (observation.Decision)
                {
                    case FrameProcessingDecision.ChangeDetected:
                        PublishState(BackgroundScannerState.ChangeDetected, "Meaningful frame change detected.");
                        continue;
                    case FrameProcessingDecision.Stabilizing:
                        PublishState(BackgroundScannerState.Stabilizing, "Waiting briefly for the changed frame to stabilize.");
                        continue;
                    case FrameProcessingDecision.IgnoredUnchanged:
                        Interlocked.Increment(ref _framesIgnored);
                        PublishDisappearanceFrameIfNeeded();
                        continue;
                    case FrameProcessingDecision.ReadyForOcr:
                        break;
                }

                PublishState(BackgroundScannerState.ReadingHeading, "Reading the payout heading.");
                Interlocked.Increment(ref _ocrScans);
                var analysis = _detector.AnalyzeFastTotalCash(frame, _mode, cancellationToken);
                if (analysis.Candidate is not null && _burstReader is not null)
                {
                    PublishState(BackgroundScannerState.ReadingAmount, "Confirming the amount across a short stable frame burst.");
                    var burstFrames = CaptureBurst(
                        frame,
                        GtaLargeDigitReaderService.MinimumBurstFrames,
                        cancellationToken).ToList();
                    var burstResult = _burstReader.Analyze(
                        burstFrames,
                        _mode,
                        cancellationToken,
                        analysis);
                    if (burstResult.NeedsTargetedFallback)
                    {
                        PublishState(BackgroundScannerState.FallbackAnalysis, "Running bounded targeted verification.");
                        burstFrames.AddRange(CaptureBurst(
                            null,
                            GtaLargeDigitReaderService.MaximumBurstFrames - burstFrames.Count,
                            cancellationToken));
                        burstResult = _burstReader.Analyze(
                            burstFrames,
                            _mode,
                            cancellationToken,
                            burstResult.Analysis);
                    }

                    analysis = burstResult.Analysis;
                }
                var performance = analysis.GtaResultDiagnostics?.Performance;
                if (performance is not null)
                {
                    performance = performance with
                    {
                        CaptureBackend = _capture.BackendDisplayName,
                        FrameChangePercentage = observation.ChangePercentage,
                        FrameStabilityMilliseconds = observation.StableFor.TotalMilliseconds,
                        FramesReceived = Interlocked.Read(ref _framesReceived),
                        FramesIgnoredAsUnchanged = Interlocked.Read(ref _framesIgnored),
                        OcrScansPerformed = Interlocked.Read(ref _ocrScans),
                        FrameChangeDetectionMilliseconds = observation.DetectionMilliseconds,
                        LastSuccessfulDetectionUtc = analysis.Candidate is null
                            ? _lastSuccessfulDetectionUtc
                            : DateTime.UtcNow
                    };
                    analysis = analysis with
                    {
                        GtaResultDiagnostics = analysis.GtaResultDiagnostics! with { Performance = performance }
                    };
                }

                if (analysis.Candidate is not null)
                {
                    _lastSuccessfulDetectionUtc = DateTime.UtcNow;
                    _pendingDisappearanceAnalysis = null;
                    _disappearanceFrames = 0;
                    PublishState(BackgroundScannerState.PayoutDetected, "Payout detected.");
                }
                else
                {
                    _pendingDisappearanceAnalysis = analysis;
                    _disappearanceFrames = 1;
                    PublishState(BackgroundScannerState.Watching, "No anchored payout found in the changed frame.");
                }

                AnalysisCompleted?.Invoke(this, new ScannerAnalysisEventArgs(analysis, Snapshot));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            PublishState(BackgroundScannerState.Error, $"Scanner failed: {ex.Message}");
        }
    }

    private IReadOnlyList<BitmapSource> CaptureBurst(
        BitmapSource? firstFrame,
        int targetFrameCount,
        CancellationToken cancellationToken)
    {
        var frames = new List<BitmapSource>(targetFrameCount);
        if (firstFrame is not null)
        {
            frames.Add(firstFrame);
        }

        while (frames.Count < targetFrameCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(75));
            cancellationToken.ThrowIfCancellationRequested();
            frames.Add(_capture.CaptureFrame());
        }

        return frames;
    }

    private void PublishDisappearanceFrameIfNeeded()
    {
        if (_pendingDisappearanceAnalysis is null ||
            _disappearanceFrames >= FrameChangeDetector.DisappearanceFrameCount)
        {
            return;
        }

        _disappearanceFrames++;
        AnalysisCompleted?.Invoke(
            this,
            new ScannerAnalysisEventArgs(_pendingDisappearanceAnalysis, Snapshot));
        if (_disappearanceFrames >= FrameChangeDetector.DisappearanceFrameCount)
        {
            _pendingDisappearanceAnalysis = null;
        }
    }

    private async Task StopCoreAsync()
    {
        var cancellation = _sessionCancellation;
        _sessionCancellation = null;
        if (cancellation is not null)
        {
            cancellation.Cancel();
        }

        var tasks = new[] { _captureTask, _processingTask }.Where(task => task is not null).Cast<Task>();
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
        }

        _captureTask = null;
        _processingTask = null;
        _frames = null;
        cancellation?.Dispose();
        _capture.Stop();
        PublishState(BackgroundScannerState.Disabled, "Scanner disabled.");
    }

    private void PublishState(BackgroundScannerState state, string message)
    {
        Snapshot = CreateSnapshot(state, message);
        SnapshotChanged?.Invoke(this, new ScannerSnapshotEventArgs(Snapshot));
    }

    private BackgroundScannerSnapshot CreateSnapshot(BackgroundScannerState state, string message)
    {
        return new BackgroundScannerSnapshot(
            state,
            _capture.BackendDisplayName,
            message,
            _lastChangePercentage,
            _lastStableFor,
            Interlocked.Read(ref _framesReceived),
            Interlocked.Read(ref _framesIgnored),
            Interlocked.Read(ref _ocrScans),
            _lastSuccessfulDetectionUtc);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _capture.Dispose();
        _lifecycleLock.Dispose();
    }
}
