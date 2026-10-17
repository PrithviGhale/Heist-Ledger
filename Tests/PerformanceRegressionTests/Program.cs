using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GTAEarningsTracker.Models;
using GTAEarningsTracker.Services;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            TestSelectionBounds();
            TestExactRecognitionCache();
            if (args.Contains("--capture", StringComparer.OrdinalIgnoreCase)) TestCapture();
            if (args.Contains("--layout", StringComparer.OrdinalIgnoreCase)) TestLayout();
            Console.WriteLine("All performance and capture correctness tests passed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void TestSelectionBounds()
    {
        var screen = new CaptureSettings { X = -1920, Y = 0, Width = 3840, Height = 1080 };
        foreach (var scale in new[] { 1.0, 1.25, 1.5 })
        {
            var start = new Point(100.2 * scale, 80.4 * scale);
            var end = new Point(400.1 * scale, 220.8 * scale);
            var selection = ScreenCaptureService.CreateEnclosingPixelBounds(start, end, screen);
            var reverse = ScreenCaptureService.CreateEnclosingPixelBounds(end, start, screen);
            Assert(selection.X <= start.X && selection.Y <= start.Y &&
                selection.X + selection.Width >= end.X && selection.Y + selection.Height >= end.Y,
                "fractional DPI selection preserves the outer pixels");
            Assert(selection.X == reverse.X && selection.Y == reverse.Y &&
                selection.Width == reverse.Width && selection.Height == reverse.Height, "reverse dragging produces identical pixel bounds");
        }
        var clamped = ScreenCaptureService.CreateEnclosingPixelBounds(new Point(-3000, -100), new Point(3000, 2000), screen);
        Assert(clamped.X == screen.X && clamped.Y == screen.Y && clamped.Width == screen.Width && clamped.Height == screen.Height,
            "selection clamps to monitor bounds, including negative monitor coordinates");
        Console.WriteLine("PASS: selection rounds outward at 100%, 125%, 150%, reverse drag and screen-bound clamping");
    }

    private static void TestExactRecognitionCache()
    {
        using var ocr = new OcrService(OcrWorkerKind.Amount);
        var original = RenderAmount("$500,000");
        var first = ocr.RecognizeAmount(original, OcrAmountPageSegmentation.SingleLine, CancellationToken.None);
        Assert(first.Text.Contains("500,000", StringComparison.Ordinal) || first.Text.Contains("500000", StringComparison.Ordinal), "initial OCR value");
        var hits = ocr.RecognitionCacheHits;
        var elapsed = Stopwatch.StartNew();
        for (var index = 0; index < 100; index++)
        {
            var repeated = ocr.RecognizeAmount(original with { Method = "Repeated frame" }, OcrAmountPageSegmentation.SingleLine, CancellationToken.None);
            Assert(repeated.Text == first.Text && repeated.PreprocessingMethod == "Repeated frame", "exact result and current diagnostic label");
        }
        Assert(ocr.RecognitionCacheHits == hits + 100, "identical frames reuse recognition");
        Console.WriteLine($"PASS: 100 exact-image recognitions reused safely in {elapsed.Elapsed.TotalMilliseconds:F1} ms");
        hits = ocr.RecognitionCacheHits;
        _ = ocr.RecognizeAmount(original, OcrAmountPageSegmentation.RawLine, CancellationToken.None);
        Assert(ocr.RecognitionCacheHits == hits, "page segmentation modes are isolated");
        _ = ocr.RecognizeHeading(original, CancellationToken.None);
        Assert(ocr.RecognitionCacheHits == hits, "heading and amount whitelist are isolated");
        var changed = ocr.RecognizeAmount(RenderAmount("$600,000"), OcrAmountPageSegmentation.SingleLine, CancellationToken.None);
        Assert(ocr.RecognitionCacheHits == hits && changed.Text != first.Text, "changed pixels cannot reuse old payout");
        var negative = ocr.RecognizeAmount(RenderAmount("-$500,000"), OcrAmountPageSegmentation.SingleLine, CancellationToken.None);
        Assert(negative.Text.Contains('-'), "negative marker survives recognition");
        try
        {
            _ = ocr.RecognizeAmount(original, OcrAmountPageSegmentation.SingleLine, new CancellationToken(true));
            throw new InvalidOperationException("Cancelled cache request was accepted.");
        }
        catch (OperationCanceledException) { }
        // Force eviction; the oldest frame must return to the native OCR engine.
        for (var index = 0; index < 66; index++)
            _ = ocr.RecognizeAmount(RenderAmount($"${700_000 + index}"), OcrAmountPageSegmentation.SingleLine, CancellationToken.None);
        hits = ocr.RecognitionCacheHits;
        _ = ocr.RecognizeAmount(original, OcrAmountPageSegmentation.SingleLine, CancellationToken.None);
        Assert(ocr.RecognitionCacheHits == hits, "recognition cache remains bounded");
        Console.WriteLine("PASS: configuration isolation, changed/negative amounts, cancellation, bounded eviction");
    }

    private static void TestCapture()
    {
        // Capture only this test's own visible colored surface; no desktop
        // images, user files, settings, or earnings are read or written.
        var top = new Border { Background = Brushes.Red };
        var bottom = new Border { Background = Brushes.Lime };
        var surface = new Grid();
        surface.RowDefinitions.Add(new RowDefinition());
        surface.RowDefinitions.Add(new RowDefinition());
        surface.Children.Add(top);
        Grid.SetRow(bottom, 1);
        surface.Children.Add(bottom);
        var window = new Window
        {
            Title = "Heist Ledger capture regression surface",
            Width = 500, Height = 240, Left = 40, Top = 40,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            Topmost = true, ShowInTaskbar = false, Content = surface
        };
        try
        {
            window.Show();
            Flush(window);
            var origin = surface.PointToScreen(new Point(0, 0));
            var dpi = VisualTreeHelper.GetDpi(surface);
            var bounds = new CaptureSettings
            {
                X = (int)Math.Round(origin.X), Y = (int)Math.Round(origin.Y),
                Width = (int)Math.Round(surface.ActualWidth * dpi.DpiScaleX),
                Height = (int)Math.Round(surface.ActualHeight * dpi.DpiScaleY)
            };
            var capture = new ScreenCaptureService();
            using (var session = capture.OpenSession(bounds))
            {
                var first = session.CaptureFrame();
                Assert(first.PixelWidth == bounds.Width && first.PixelHeight == bounds.Height && first.IsFrozen, "native size and immutable frame");
                AssertColor(first, 10, 10, 255, 0, 0);
                AssertColor(first, 10, first.PixelHeight - 10, 0, 255, 0);
                top.Background = Brushes.Blue;
                Flush(window);
                var second = session.CaptureFrame();
                AssertColor(second, 10, 10, 0, 0, 255);
                AssertColor(first, 10, 10, 255, 0, 0);
                var excluded = capture.BlackOutRegions(second, bounds,
                    [new Int32Rect(bounds.X, bounds.Y, 30, 30)]);
                AssertColor(excluded, 10, 10, 0, 0, 0);
            }
            Console.WriteLine($"PASS: top-down capture at {dpi.PixelsPerDip:P0} DPI, exact size/color, independent burst frames, exclusions");
            using var process = Process.GetCurrentProcess();
            var handlesBefore = GetGuiResources(process.Handle, 0);
            for (var index = 0; index < 50; index++)
            {
                using var session = capture.OpenSession(bounds);
                for (var frame = 0; frame < 5; frame++) _ = session.CaptureFrame();
            }
            var handlesAfter = GetGuiResources(process.Handle, 0);
            Assert(handlesAfter <= handlesBefore + 2, $"GDI resource leak: before={handlesBefore}, after={handlesAfter}");
            var disposed = capture.OpenSession(bounds);
            disposed.Dispose();
            disposed.Dispose();
            try { _ = disposed.CaptureFrame(); throw new InvalidOperationException("Disposed capture succeeded."); }
            catch (ObjectDisposedException) { }
            Assert(!capture.IsValidCaptureArea(new CaptureSettings { X = int.MaxValue, Y = 0, Width = 100, Height = 40 }), "out of bounds coordinates rejected");
            Console.WriteLine($"PASS: 250 frames / 50 sessions with no GDI leak ({handlesBefore} -> {handlesAfter}), disposal and invalid bounds");
            var timer = Stopwatch.StartNew();
            for (var index = 0; index < 40; index++) _ = capture.Capture(bounds);
            var singleMs = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            using (var burst = capture.OpenSession(bounds))
                for (var index = 0; index < 40; index++) _ = burst.CaptureFrame();
            Console.WriteLine($"Capture benchmark (40 frames): individual sessions={singleMs:F1} ms; reused session={timer.Elapsed.TotalMilliseconds:F1} ms");
        }
        finally { window.Close(); }
    }

    private static void TestLayout()
    {
        // Render the real XAML offscreen. Do not show the main window or run its
        // Loaded handler: this must not touch the user's ledger or settings.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var resource in new[] { "Colors", "Styles" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/HeistLedger;component/Themes/{resource}.xaml")
            });
        var window = new GTAEarningsTracker.MainWindow();
        var host = (FrameworkElement)window.Content;
        window.Content = null;
        // Detached visuals are not in Application.Windows, so attach the same
        // resource dictionary explicitly for live theme invalidation in this renderer.
        host.Resources = app.Resources;
        FrameworkElement Element(string name) => (FrameworkElement)window.FindName(name);
        Element("LoadingOverlay").Visibility = Visibility.Collapsed;
        Element("CaptureSetupPanel").Visibility = Visibility.Collapsed;
        var output = Path.Combine(Environment.CurrentDirectory, "artifacts", "performance-update", "layout");
        Directory.CreateDirectory(output);
        foreach (var theme in UiThemeService.ThemeNames)
        {
            _ = new UiThemeService().Apply(theme);
            foreach (var mode in new[] { "dashboard", "compact", "confirmation", "settings", "intro", "compact-intro" })
            {
                var compact = mode is "compact" or "confirmation" or "compact-intro";
                var intro = mode is "intro" or "compact-intro";
                Element("LoadingOverlay").Visibility = intro ? Visibility.Visible : Visibility.Collapsed;
                Element("ExpandedDashboard").Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
                Element("CompactDashboard").Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
                Element("CompactNormalPanel").Visibility = mode == "confirmation" ? Visibility.Collapsed : Visibility.Visible;
                Element("CompactPendingIndicator").Visibility = mode == "confirmation" ? Visibility.Visible : Visibility.Collapsed;
                Element("MainScrollViewer").Visibility = mode == "settings" ? Visibility.Collapsed : Visibility.Visible;
                Element("SettingsScrollViewer").Visibility = mode == "settings" ? Visibility.Visible : Visibility.Collapsed;
                Element("SettingsUpdateBadge").Visibility = mode == "settings" ? Visibility.Visible : Visibility.Collapsed;
                ((TextBlock)Element("DayTotalText")).Text = "$12,418,670";
                ((TextBlock)Element("CompactPendingText")).Text = "$1,818,663";
                var size = compact
                    ? new Size(420, mode == "confirmation" ? 610 : 365)
                    : new Size(500, 760);
                host.Measure(size);
                host.Arrange(new Rect(size));
                host.UpdateLayout();
                if (mode == "settings")
                {
                    var settingsViewer = (ScrollViewer)Element("SettingsScrollViewer");
                    Assert(settingsViewer.ExtentWidth <= settingsViewer.ViewportWidth + 1, "settings must not overflow horizontally");
                }
                host.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                var expectedAccent = (SolidColorBrush)app.Resources["EarningsGreenBrush"];
                var actualAccent = (SolidColorBrush)((TextBlock)Element("DayTotalText")).Foreground;
                Assert(expectedAccent.Color == actualAccent.Color, $"{theme}: theme resources update correctly");
                var buttonNames = intro ? new[] { "LoadingContent" } : mode == "compact"
                    ? new[] { "CompactQuickCaptureButton" }
                    : mode == "confirmation"
                        ? new[] { "CompactAcceptButton", "CompactEditButton", "CompactRescanButton", "CompactRejectButton" }
                        : mode == "dashboard" ? new[] { "QuickCaptureButton" } : new[] { "SettingsTabButton" };
                foreach (var name in buttonNames)
                {
                    var button = Element(name);
                    var rect = button.TransformToAncestor(host).TransformBounds(new Rect(button.RenderSize));
                    Assert(rect.Width > 20 && rect.Height >= 30 && rect.Top >= 0 && rect.Bottom <= size.Height && rect.Right <= size.Width,
                        $"{theme} {mode}: {name} must be fully visible ({rect})");
                }
                foreach (var scale in new[] { 1.0, 1.25, 1.5 })
                {
                    var bitmap = new RenderTargetBitmap((int)(size.Width * scale), (int)(size.Height * scale),
                        96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    bitmap.Render(host);
                    var corner = new byte[4];
                    bitmap.CopyPixels(new Int32Rect(8 * (int)Math.Ceiling(scale), 8 * (int)Math.Ceiling(scale), 1, 1), corner, 4, 0);
                    Assert(corner[3] < 100, "outer corners must remain transparent");
                    if (theme is "Galaxy" or "Dark")
                    {
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var file = File.Create(Path.Combine(output, $"{theme}-{mode}-{scale * 100:0}.png"));
                        encoder.Save(file);
                    }
                }
            }
        }
        Console.WriteLine("PASS: real XAML renders in all 6 themes at 100%, 125%, 150%; dashboard/compact/confirmation/settings and both intro sizes fit; rounded corners stay transparent");
        Element("LoadingOverlay").Visibility = Visibility.Collapsed;
        TestTransitions(window, host);
    }

    private static void TestTransitions(GTAEarningsTracker.MainWindow window, FrameworkElement host)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(host.Dispatcher));
        object? Invoke(string name, params object[] arguments) => window.GetType()
            .GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(window, arguments);
        void Pump(Task task)
        {
            var frame = new DispatcherFrame();
            var timeout = Stopwatch.StartNew();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { host.UpdateLayout(); if (task.IsCompleted || timeout.Elapsed > TimeSpan.FromSeconds(5)) frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
            timer.Stop();
            Assert(task.IsCompleted, "animation must finish without hanging");
            task.GetAwaiter().GetResult();
        }
        // Invoke presentation-only methods on the detached visual; never show the
        // window, run startup, or call handlers that persist personal settings.
        Invoke("ApplyWindowMode", false, true);
        var screen = new ScreenCaptureService().GetPrimaryScreenBounds();
        var capture = (CaptureSettings)Invoke("GetAutomaticCaptureArea")!;
        Assert(capture.X == screen.X && capture.Y == screen.Y && capture.Width == screen.Width && capture.Height == screen.Height,
            "Quick Scan defaults to the full native primary display without a saved selection");
        Invoke("SetUpdateNotification", "9.0.0");
        Assert(((FrameworkElement)window.FindName("SettingsUpdateBadge")).Visibility == Visibility.Visible,
            "newer update displays a Settings notification");
        Invoke("SetUpdateNotification", "1.0.0");
        Assert(((FrameworkElement)window.FindName("SettingsUpdateBadge")).Visibility == Visibility.Collapsed,
            "installed or older updates clear the notification");
        Invoke("SelectTab", "Dashboard", false);
        host.Measure(new Size(500, 760));
        host.Arrange(new Rect(0, 0, 500, 760));
        host.UpdateLayout();
        var forward = (Task)Invoke("SelectTabAnimatedAsync", "Settings")!;
        _ = Invoke("SelectTabAnimatedAsync", "Dashboard");
        Pump(forward);
        var home = (ScrollViewer)window.FindName("MainScrollViewer");
        var settings = (ScrollViewer)window.FindName("SettingsScrollViewer");
        Assert(home.Visibility == Visibility.Visible && settings.Visibility == Visibility.Collapsed,
            "rapid tab requests settle on the last selected page");
        Pump((Task)Invoke("SelectTabAnimatedAsync", "Settings")!);
        Assert(settings.Visibility == Visibility.Visible && ((FrameworkElement)window.FindName("SettingsBackButton")).Visibility == Visibility.Visible,
            "settings page exposes back navigation after sliding");
        GTAEarningsTracker.Behaviors.SmoothScrollBehavior.ScrollTo(settings, 300);
        Pump(Task.Delay(260));
        Assert(Math.Abs(settings.VerticalOffset - 300) < 2, "smooth settings navigation reaches its target");
        settings.ScrollToVerticalOffset(40);
        Pump(Task.Delay(80));
        Assert(Math.Abs(settings.VerticalOffset - 40) < 2, "finished scrolling releases its clock and leaves native scrolling usable");
        for (var index = 0; index < 2; index++)
        {
            Pump((Task)Invoke("AnimateWindowSizeAsync", 420d, 365d, true)!);
            Assert(window.Width == 420 && window.Height == 365 && window.MaxWidth == 430, "compact resize settles once at final bounds");
            Pump((Task)Invoke("AnimateWindowSizeAsync", 500d, 760d, false)!);
            Assert(window.Width == 500 && window.Height == 760 && window.MaxWidth == 560, "expansion cannot retain compact width constraints");
            var chrome = (Border)window.FindName("WindowChrome");
            Assert(chrome.CacheMode is null && double.IsNaN(chrome.Width) && host.IsHitTestVisible,
                "completed transitions release bitmap caches and restore interaction and normal layout");
            Assert(((Image)window.FindName("ModeTransitionPreview")).Source is null,
                "resize snapshot is released after the single transition");
        }
        Console.WriteLine("PASS: header settings/back navigation, smooth scrolling, update badge, compact/full transitions and animation cleanup");
    }

    private static void Flush(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        ScreenCaptureService.WaitForDesktopComposition();
    }

    private static void AssertColor(BitmapSource frame, int x, int y, byte red, byte green, byte blue)
    {
        var pixel = new byte[4];
        frame.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        Assert(pixel[0] == blue && pixel[1] == green && pixel[2] == red,
            $"expected RGB {red},{green},{blue}; captured {pixel[2]},{pixel[1]},{pixel[0]}");
    }

    private static PreprocessedImage RenderAmount(string amount)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 560, 100));
            drawing.DrawText(new FormattedText(amount, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Arial"), 64, Brushes.Black, 1), new Point(16, 8));
        }
        var bitmap = new RenderTargetBitmap(560, 100, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return new PreprocessedImage("Generated cache test", stream.ToArray());
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);
}
