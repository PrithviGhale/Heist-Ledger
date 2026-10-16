using System.IO;
using System.Security.Cryptography;
using Tesseract;

namespace GTAEarningsTracker.Services;

public sealed record OcrTextLine(
    string Text,
    float Confidence,
    int? Top,
    int? Bottom,
    int? Left = null,
    int? Right = null);

public sealed record OcrSymbol(
    string Text,
    float Confidence,
    int Left,
    int Top,
    int Right,
    int Bottom);

public enum OcrAmountPageSegmentation
{
    SingleLine,
    RawLine
}

public enum OcrWorkerKind
{
    General,
    Heading,
    Amount
}

public sealed record OcrRecognitionResult(
    string Text,
    float Confidence,
    string PreprocessingMethod,
    IReadOnlyList<OcrTextLine>? TextLines = null,
    IReadOnlyList<OcrSymbol>? Symbols = null);

public sealed class OcrService : IDisposable
{
    private const string AllowedCharacters =
        "0123456789$,+-.ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    private readonly object _syncRoot = new();
    private readonly OcrWorkerKind _workerKind;
    private TesseractEngine? _engine;
    private bool _disposed;
    private const int RecognitionCacheCapacity = 64;
    private readonly Dictionary<string, OcrRecognitionResult> _recognitionCache = new(StringComparer.Ordinal);
    private readonly Queue<string> _recognitionCacheOrder = new();

    public int RecognitionCacheHits { get; private set; }

    public OcrService(OcrWorkerKind workerKind = OcrWorkerKind.General)
    {
        _workerKind = workerKind;
        TessdataPath = Path.Combine(AppContext.BaseDirectory, "tessdata");
    }

    public string TessdataPath { get; }

    public bool IsInitialized
    {
        get
        {
            lock (_syncRoot)
            {
                return !_disposed && _engine is not null;
            }
        }
    }

    public OcrRecognitionResult Recognize(PreprocessedImage image, CancellationToken cancellationToken)
    {
        return Recognize(image, PageSegMode.SingleLine, includeTextLines: false, cancellationToken);
    }

    public OcrRecognitionResult RecognizeMultiline(
        PreprocessedImage image,
        CancellationToken cancellationToken)
    {
        return Recognize(image, PageSegMode.SparseText, includeTextLines: true, cancellationToken);
    }

    public OcrRecognitionResult RecognizeHeading(
        PreprocessedImage image,
        CancellationToken cancellationToken)
    {
        return Recognize(
            image,
            PageSegMode.SparseText,
            includeTextLines: true,
            cancellationToken,
            "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 ");
    }

    public OcrRecognitionResult RecognizeAmount(
        PreprocessedImage image,
        OcrAmountPageSegmentation segmentationMode,
        CancellationToken cancellationToken)
    {
        var pageSegmentationMode = segmentationMode == OcrAmountPageSegmentation.RawLine
            ? PageSegMode.RawLine
            : PageSegMode.SingleLine;
        return Recognize(
            image,
            pageSegmentationMode,
            includeTextLines: true,
            cancellationToken,
            // The minus sign is retained as a safety-only extension so a negative
            // result cannot become a positive payout when OCR drops punctuation.
            "$0123456789,.-");
    }

    public OcrRecognitionResult RecognizeAmountCharacter(
        PreprocessedImage image,
        CancellationToken cancellationToken)
    {
        return Recognize(
            image,
            PageSegMode.SingleChar,
            includeTextLines: true,
            cancellationToken,
            "0123456789");
    }

    private OcrRecognitionResult Recognize(
        PreprocessedImage image,
        PageSegMode pageSegMode,
        bool includeTextLines,
        CancellationToken cancellationToken,
        string whitelist = AllowedCharacters)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();

            // Cache only an exact PNG plus the complete recognition configuration.
            // Fresh frames with even one changed pixel still run through OCR. No
            // image bytes or screen coordinates are retained in this bounded cache.
            var cacheKey = $"{pageSegMode}:{includeTextLines}:{whitelist}:{Convert.ToHexString(SHA256.HashData(image.PngBytes))}";
            if (_recognitionCache.TryGetValue(cacheKey, out var cached))
            {
                RecognitionCacheHits++;
                return cached with { PreprocessingMethod = image.Method };
            }

            var engine = GetOrCreateEngine();
            engine.SetVariable("tessedit_char_whitelist", whitelist);
            try
            {
                using var pix = Pix.LoadFromMemory(image.PngBytes);
                if (pix.Width <= 0 || pix.Height <= 0)
                {
                    throw new InvalidOperationException("OCR received an empty image.");
                }

                using var page = engine.Process(pix, pageSegMode);
                var text = page.GetText()?.Trim() ?? string.Empty;
                var lines = includeTextLines ? ReadTextLines(page) : null;
                var symbols = includeTextLines ? ReadSymbols(page) : null;
                var result = new OcrRecognitionResult(
                    text,
                    page.GetMeanConfidence(),
                    image.Method,
                    lines,
                    symbols);
                if (_recognitionCache.Count >= RecognitionCacheCapacity)
                {
                    _recognitionCache.Remove(_recognitionCacheOrder.Dequeue());
                }
                _recognitionCache.Add(cacheKey, result);
                _recognitionCacheOrder.Enqueue(cacheKey);
                return result;
            }
            finally
            {
                engine.SetVariable("tessedit_char_whitelist", GetDefaultWhitelist());
            }
        }
    }

    private static IReadOnlyList<OcrTextLine> ReadTextLines(Page page)
    {
        var lines = new List<OcrTextLine>();
        using var iterator = page.GetIterator();
        iterator.Begin();

        do
        {
            var text = iterator.GetText(PageIteratorLevel.TextLine)?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            int? top = null;
            int? bottom = null;
            int? left = null;
            int? right = null;
            if (iterator.TryGetBoundingBox(PageIteratorLevel.TextLine, out var bounds))
            {
                left = bounds.X1;
                top = bounds.Y1;
                right = bounds.X2;
                bottom = bounds.Y2;
            }

            var confidence = iterator.GetConfidence(PageIteratorLevel.TextLine);
            if (confidence > 1)
            {
                confidence /= 100;
            }

            lines.Add(new OcrTextLine(text, confidence, top, bottom, left, right));
        }
        while (iterator.Next(PageIteratorLevel.TextLine));

        return lines;
    }

    private static IReadOnlyList<OcrSymbol> ReadSymbols(Page page)
    {
        var symbols = new List<OcrSymbol>();
        using var iterator = page.GetIterator();
        iterator.Begin();
        do
        {
            var text = iterator.GetText(PageIteratorLevel.Symbol)?.Trim();
            if (string.IsNullOrWhiteSpace(text) ||
                !iterator.TryGetBoundingBox(PageIteratorLevel.Symbol, out var bounds))
            {
                continue;
            }

            var confidence = iterator.GetConfidence(PageIteratorLevel.Symbol);
            if (confidence > 1)
            {
                confidence /= 100;
            }

            symbols.Add(new OcrSymbol(
                text,
                confidence,
                bounds.X1,
                bounds.Y1,
                bounds.X2,
                bounds.Y2));
        }
        while (iterator.Next(PageIteratorLevel.Symbol));

        return symbols;
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
            _engine?.Dispose();
            _engine = null;
            _recognitionCache.Clear();
            _recognitionCacheOrder.Clear();
        }
    }

    private TesseractEngine GetOrCreateEngine()
    {
        if (_engine is not null)
        {
            return _engine;
        }

        var trainedDataFile = Path.Combine(TessdataPath, "eng.traineddata");
        if (!File.Exists(trainedDataFile))
        {
            throw new FileNotFoundException(
                "English OCR data is missing. Rebuild the application and verify tessdata/eng.traineddata is copied to the output folder.",
                trainedDataFile);
        }

        try
        {
            _engine = new TesseractEngine(TessdataPath, "eng", EngineMode.LstmOnly);
            _engine.SetVariable("tessedit_char_whitelist", GetDefaultWhitelist());
            _engine.SetVariable("preserve_interword_spaces", "1");
            if (_workerKind is OcrWorkerKind.Heading or OcrWorkerKind.Amount)
            {
                _engine.SetVariable("load_system_dawg", "0");
                _engine.SetVariable("load_freq_dawg", "0");
                _engine.SetVariable("load_unambig_dawg", "0");
                _engine.SetVariable("load_punc_dawg", "0");
                _engine.SetVariable("load_number_dawg", "0");
            }

            return _engine;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "The local OCR engine could not start. Verify tessdata and install the Microsoft Visual C++ 2015–2022 Redistributable.",
                ex);
        }
    }

    private string GetDefaultWhitelist()
    {
        return _workerKind switch
        {
            OcrWorkerKind.Heading => "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 ",
            OcrWorkerKind.Amount => "$0123456789,.-",
            _ => AllowedCharacters
        };
    }
}
