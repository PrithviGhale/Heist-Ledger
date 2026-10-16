using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace GTAEarningsTracker.Services;

public sealed record PaddleOcrRecognitionResult(
    string RawText,
    string NormalizedDigits,
    float Confidence,
    TimeSpan ProcessingDuration,
    string? Error = null)
{
    public bool IsValid => Error is null && NormalizedDigits.Length > 0;

    public long? ParsedAmount => long.TryParse(
        NormalizedDigits,
        NumberStyles.None,
        CultureInfo.InvariantCulture,
        out var amount)
            ? amount
            : null;
}

/// <summary>
/// Reusable, CPU-only PP-OCRv5 text-line recognizer. No image or recognition
/// data leaves the process.
/// </summary>
public sealed class PaddleOcrRecognitionService : IDisposable
{
    public const string ModelDisplayName = "PP-OCRv5_mobile_rec_onnx";
    public const string UnavailableMessage = "AI OCR unavailable — using standard scanner.";

    private const int InputHeight = 48;
    private const int MaximumInputWidth = 320;
    private static readonly Regex AcceptedOutputRegex = new(
        @"^\$?(?:[0-9]+|[0-9]{1,3}(?<separator>[,.])[0-9]{3}(?:\k<separator>[0-9]{3})*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly object _syncRoot = new();
    private readonly string _modelPath;
    private readonly string _dictionaryPath;
    private InferenceSession? _session;
    private string[]? _characters;
    private string? _loadError;
    private bool _loadAttempted;
    private bool _disposed;
    private TimeSpan? _modelLoadDuration;
    private TimeSpan? _latestProcessingDuration;

    public bool Enabled { get; set; } = true;

    public PaddleOcrRecognitionService(string? modelDirectory = null)
    {
        modelDirectory ??= Path.Combine(AppContext.BaseDirectory, "Models", "Ocr", "Paddle");
        _modelPath = Path.Combine(modelDirectory, "inference.onnx");
        _dictionaryPath = Path.Combine(modelDirectory, "character_dictionary.txt");
    }

    public bool IsAvailable
    {
        get
        {
            if (!Enabled)
            {
                return false;
            }
            EnsureInitialized();
            lock (_syncRoot)
            {
                return _session is not null;
            }
        }
    }

    public string ModelStatus
    {
        get
        {
            if (!Enabled)
            {
                return "Disabled";
            }
            EnsureInitialized();
            lock (_syncRoot)
            {
                return _session is not null ? "Ready (local CPU)" : UnavailableMessage;
            }
        }
    }

    public string? LoadError
    {
        get
        {
            EnsureInitialized();
            lock (_syncRoot)
            {
                return _loadError;
            }
        }
    }

    public TimeSpan? ModelLoadDuration
    {
        get { lock (_syncRoot) { return _modelLoadDuration; } }
    }

    public TimeSpan? LatestProcessingDuration
    {
        get { lock (_syncRoot) { return _latestProcessingDuration; } }
    }

    public Task<PaddleOcrRecognitionResult> RecognizeAsync(
        BitmapSource amountCrop,
        CancellationToken cancellationToken) =>
        Task.Run(() => Recognize(amountCrop, cancellationToken), cancellationToken);

    public PaddleOcrRecognitionResult Recognize(
        BitmapSource amountCrop,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(amountCrop);
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();

        if (!Enabled)
        {
            stopwatch.Stop();
            return new PaddleOcrRecognitionResult(
                string.Empty,
                string.Empty,
                0,
                stopwatch.Elapsed,
                "PaddleOCR is disabled in settings.");
        }

        EnsureInitialized();
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is null || _characters is null)
            {
                return Complete(string.Empty, string.Empty, 0, _loadError ?? UnavailableMessage);
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var tensor = CreateInputTensor(amountCrop);
                var inputName = _session.InputMetadata.Keys.Single();
                var input = NamedOnnxValue.CreateFromTensor(inputName, tensor);
                using var results = _session.Run([input]);
                cancellationToken.ThrowIfCancellationRequested();

                var output = results.First().AsTensor<float>();
                var decoded = DecodeCtc(output, _characters);
                var normalized = NormalizeDigits(decoded.Text);
                return Complete(decoded.Text, normalized, decoded.Confidence, null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Complete(string.Empty, string.Empty, 0, ex.Message);
            }
        }

        PaddleOcrRecognitionResult Complete(
            string rawText,
            string normalizedDigits,
            float confidence,
            string? error)
        {
            stopwatch.Stop();
            _latestProcessingDuration = stopwatch.Elapsed;
            return new PaddleOcrRecognitionResult(
                rawText,
                normalizedDigits,
                confidence,
                stopwatch.Elapsed,
                error);
        }
    }

    public static string NormalizeDigits(string rawText)
    {
        var compact = new string(rawText.Where(character => !char.IsWhiteSpace(character)).ToArray());
        // PP-OCR occasionally maps GTA's stylized dollar sign to a/c/G while
        // reading every following digit correctly. Accept only that single,
        // leading currency-glyph substitution; letters inside the amount remain
        // invalid and can never influence consensus.
        if (compact.Length >= 5 && compact[0] is 'a' or 'A' or 'c' or 'C' or 'g' or 'G' &&
            compact[1..].All(char.IsDigit))
        {
            compact = compact[1..];
        }
        if (!AcceptedOutputRegex.IsMatch(compact))
        {
            return string.Empty;
        }

        var digits = new string(compact.Where(char.IsDigit).ToArray());
        return digits.Length > 0 && long.TryParse(
            digits,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out _)
                ? digits
                : string.Empty;
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _session?.Dispose();
            _session = null;
        }
    }

    private void EnsureInitialized()
    {
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_loadAttempted)
            {
                return;
            }

            _loadAttempted = true;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                if (!File.Exists(_modelPath) || !File.Exists(_dictionaryPath))
                {
                    throw new FileNotFoundException(
                        "The PaddleOCR model or character dictionary is missing.",
                        !File.Exists(_modelPath) ? _modelPath : _dictionaryPath);
                }

                _characters = File.ReadAllLines(_dictionaryPath, Encoding.UTF8);
                if (_characters.Length == 0)
                {
                    throw new InvalidDataException("The PaddleOCR character dictionary is empty.");
                }

                using var options = new SessionOptions
                {
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 4, 1, 2),
                    InterOpNumThreads = 1,
                    LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
                };
                // This is an on-demand companion: idle OCR workers must sleep,
                // rather than spin and compete with the game for CPU time.
                options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
                options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
                _session = new InferenceSession(_modelPath, options);
                _loadError = null;
            }
            catch (Exception ex)
            {
                _session?.Dispose();
                _session = null;
                _characters = null;
                _loadError = ex.Message;
            }
            finally
            {
                stopwatch.Stop();
                _modelLoadDuration = stopwatch.Elapsed;
            }
        }
    }

    private static DenseTensor<float> CreateInputTensor(BitmapSource source)
    {
        var scale = InputHeight / (double)Math.Max(1, source.PixelHeight);
        var resizedWidth = Math.Clamp((int)Math.Ceiling(source.PixelWidth * scale), 1, MaximumInputWidth);
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);
        var resized = new TransformedBitmap(converted, new ScaleTransform(
            resizedWidth / (double)Math.Max(1, converted.PixelWidth),
            InputHeight / (double)Math.Max(1, converted.PixelHeight)));
        var stride = resizedWidth * 3;
        var pixels = new byte[stride * InputHeight];
        resized.CopyPixels(pixels, stride, 0);

        var tensor = new DenseTensor<float>([1, 3, InputHeight, MaximumInputWidth]);
        for (var y = 0; y < InputHeight; y++)
        {
            for (var x = 0; x < MaximumInputWidth; x++)
            {
                for (var channel = 0; channel < 3; channel++)
                {
                    // RecResizeImg pads with zero before normalization, producing -1.
                    var normalized = -1f;
                    if (x < resizedWidth)
                    {
                        normalized = (pixels[(y * stride) + (x * 3) + channel] / 255f - 0.5f) / 0.5f;
                    }

                    tensor[0, channel, y, x] = normalized;
                }
            }
        }

        return tensor;
    }

    private static (string Text, float Confidence) DecodeCtc(
        Tensor<float> output,
        IReadOnlyList<string> dictionary)
    {
        var dimensions = output.Dimensions.ToArray();
        if (dimensions.Length != 3 || dimensions[0] != 1 || dimensions[1] <= 0 || dimensions[2] <= 1)
        {
            throw new InvalidDataException(
                $"Unexpected PaddleOCR output shape [{string.Join(",", dimensions)}].");
        }

        var timeSteps = dimensions[1];
        var classes = dimensions[2];
        var text = new StringBuilder();
        var confidenceTotal = 0f;
        var emitted = 0;
        var previousIndex = -1;
        for (var step = 0; step < timeSteps; step++)
        {
            var bestIndex = 0;
            var bestScore = float.MinValue;
            for (var index = 0; index < classes; index++)
            {
                var score = output[0, step, index];
                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = index;
                }
            }

            // Paddle CTC reserves class zero for blank and shifts dictionary indices by one.
            if (bestIndex != 0 && bestIndex != previousIndex)
            {
                var dictionaryIndex = bestIndex - 1;
                if (dictionaryIndex < 0 || dictionaryIndex >= dictionary.Count)
                {
                    throw new InvalidDataException("PaddleOCR output referenced an unknown character.");
                }

                text.Append(dictionary[dictionaryIndex]);
                confidenceTotal += bestScore;
                emitted++;
            }

            previousIndex = bestIndex;
        }

        return (text.ToString(), emitted == 0 ? 0 : confidenceTotal / emitted);
    }
}
