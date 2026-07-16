using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GTAEarningsTracker.Services;

public enum FrameProcessingDecision
{
    IgnoredUnchanged,
    ChangeDetected,
    Stabilizing,
    ReadyForOcr
}

public sealed record FrameChangeObservation(
    FrameProcessingDecision Decision,
    double ChangePercentage,
    TimeSpan StableFor,
    double DetectionMilliseconds);

public sealed class FrameChangeDetector
{
    public const double MinimumFrameChangePercentage = 0.75;
    public static readonly TimeSpan StabilityDelay = TimeSpan.FromMilliseconds(220);
    public static readonly TimeSpan MinimumOcrInterval = TimeSpan.FromMilliseconds(750);
    public const int DisappearanceFrameCount = 2;

    private const int HashWidth = 48;
    private const int HashHeight = 27;

    private byte[]? _lastObservedHash;
    private byte[]? _lastProcessedHash;
    private DateTime _stableSinceUtc;
    private DateTime _lastOcrUtc = DateTime.MinValue;

    public FrameChangeObservation Observe(BitmapSource frame, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var stopwatch = Stopwatch.StartNew();
        var hash = CreateLuminanceHash(frame);
        if (_lastObservedHash is null)
        {
            _lastObservedHash = hash;
            _stableSinceUtc = nowUtc;
            stopwatch.Stop();
            return new FrameChangeObservation(
                FrameProcessingDecision.ChangeDetected,
                100,
                TimeSpan.Zero,
                stopwatch.Elapsed.TotalMilliseconds);
        }

        var change = CalculateDifferencePercentage(hash, _lastObservedHash);
        if (change >= MinimumFrameChangePercentage)
        {
            _lastObservedHash = hash;
            _stableSinceUtc = nowUtc;
            stopwatch.Stop();
            return new FrameChangeObservation(
                FrameProcessingDecision.ChangeDetected,
                change,
                TimeSpan.Zero,
                stopwatch.Elapsed.TotalMilliseconds);
        }

        var stableFor = nowUtc - _stableSinceUtc;
        if (stableFor < StabilityDelay)
        {
            stopwatch.Stop();
            return new FrameChangeObservation(
                FrameProcessingDecision.Stabilizing,
                change,
                stableFor,
                stopwatch.Elapsed.TotalMilliseconds);
        }

        var differsFromProcessed = _lastProcessedHash is null ||
            CalculateDifferencePercentage(hash, _lastProcessedHash) >= MinimumFrameChangePercentage;
        if (differsFromProcessed && nowUtc - _lastOcrUtc >= MinimumOcrInterval)
        {
            _lastProcessedHash = hash;
            _lastOcrUtc = nowUtc;
            stopwatch.Stop();
            return new FrameChangeObservation(
                FrameProcessingDecision.ReadyForOcr,
                change,
                stableFor,
                stopwatch.Elapsed.TotalMilliseconds);
        }

        stopwatch.Stop();
        return new FrameChangeObservation(
            FrameProcessingDecision.IgnoredUnchanged,
            change,
            stableFor,
            stopwatch.Elapsed.TotalMilliseconds);
    }

    public void Reset()
    {
        _lastObservedHash = null;
        _lastProcessedHash = null;
        _stableSinceUtc = default;
        _lastOcrUtc = DateTime.MinValue;
    }

    public static double CalculateDifferencePercentage(byte[] left, byte[] right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.Length != right.Length || left.Length == 0)
        {
            return 100;
        }

        long totalDifference = 0;
        for (var index = 0; index < left.Length; index++)
        {
            totalDifference += Math.Abs(left[index] - right[index]);
        }

        return totalDifference * 100d / (left.Length * 255d);
    }

    private static byte[] CreateLuminanceHash(BitmapSource source)
    {
        var scaleX = HashWidth / (double)source.PixelWidth;
        var scaleY = HashHeight / (double)source.PixelHeight;
        var scaled = new TransformedBitmap(source, new ScaleTransform(scaleX, scaleY));
        scaled.Freeze();
        var grayscale = new FormatConvertedBitmap(scaled, PixelFormats.Gray8, null, 0);
        grayscale.Freeze();
        var pixels = new byte[HashWidth * HashHeight];
        grayscale.CopyPixels(pixels, HashWidth, 0);
        return pixels;
    }
}
