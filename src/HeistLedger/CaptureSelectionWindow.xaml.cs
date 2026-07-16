using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using GTAEarningsTracker.Models;
using GTAEarningsTracker.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace GTAEarningsTracker;

public partial class CaptureSelectionWindow : Window
{
    private const int MinimumPayoutAreaWidth = 300;
    private const int MinimumPayoutAreaHeight = 100;

    private readonly ScreenCaptureService _screenCaptureService;
    private readonly Grid _selectionSurface;
    private readonly Border _selectionBorder;
    private readonly TextBlock _selectionStatusText;
    private Point _selectionStart;
    private bool _isSelecting;

    public CaptureSelectionWindow(ScreenCaptureService screenCaptureService)
    {
        System.Windows.Application.LoadComponent(
            this,
            new Uri("/HeistLedger;component/CaptureSelectionWindow.xaml", UriKind.Relative));
        _selectionSurface = GetRequiredElement<Grid>("SelectionSurface");
        _selectionBorder = GetRequiredElement<Border>("SelectionBorder");
        _selectionStatusText = GetRequiredElement<TextBlock>("SelectionStatusText");
        _screenCaptureService = screenCaptureService;
    }

    public CaptureSettings? SelectedArea { get; private set; }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var screenBounds = _screenCaptureService.GetPrimaryScreenBounds();
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null)
        {
            return;
        }

        var fromDevice = source.CompositionTarget.TransformFromDevice;
        var topLeft = fromDevice.Transform(new Point(screenBounds.X, screenBounds.Y));
        var bottomRight = fromDevice.Transform(new Point(
            screenBounds.X + screenBounds.Width,
            screenBounds.Y + screenBounds.Height));

        Left = topLeft.X;
        Top = topLeft.Y;
        Width = bottomRight.X - topLeft.X;
        Height = bottomRight.Y - topLeft.Y;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _selectionStart = ClampToSelectionSurface(e.GetPosition(_selectionSurface));
        _isSelecting = true;
        _selectionStatusText.Text = string.Empty;
        _selectionBorder.Visibility = Visibility.Visible;
        UpdateSelectionBorder(_selectionStart);
        Mouse.Capture(this);
        e.Handled = true;
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isSelecting || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        UpdateSelectionBorder(ClampToSelectionSurface(e.GetPosition(_selectionSurface)));
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isSelecting)
        {
            return;
        }

        var selectionEnd = ClampToSelectionSurface(e.GetPosition(_selectionSurface));
        _isSelecting = false;
        Mouse.Capture(null);
        UpdateSelectionBorder(selectionEnd);

        var left = Math.Min(_selectionStart.X, selectionEnd.X);
        var top = Math.Min(_selectionStart.Y, selectionEnd.Y);
        var right = Math.Max(_selectionStart.X, selectionEnd.X);
        var bottom = Math.Max(_selectionStart.Y, selectionEnd.Y);
        var screenTopLeft = PointToScreen(new Point(left, top));
        var screenBottomRight = PointToScreen(new Point(right, bottom));

        var selectedArea = new CaptureSettings
        {
            X = (int)Math.Round(screenTopLeft.X),
            Y = (int)Math.Round(screenTopLeft.Y),
            Width = (int)Math.Round(screenBottomRight.X - screenTopLeft.X),
            Height = (int)Math.Round(screenBottomRight.Y - screenTopLeft.Y)
        };

        if (!_screenCaptureService.IsValidCaptureArea(selectedArea) ||
            selectedArea.Width < MinimumPayoutAreaWidth ||
            selectedArea.Height < MinimumPayoutAreaHeight)
        {
            _selectionStatusText.Text =
                "Include both text lines with padding. The area must be at least 300 by 100 pixels.";
            return;
        }

        SelectedArea = selectedArea;
        DialogResult = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        if (_isSelecting)
        {
            _isSelecting = false;
            Mouse.Capture(null);
        }

        DialogResult = false;
        e.Handled = true;
    }

    private Point ClampToSelectionSurface(Point point)
    {
        return new Point(
            Math.Clamp(point.X, 0, _selectionSurface.ActualWidth),
            Math.Clamp(point.Y, 0, _selectionSurface.ActualHeight));
    }

    private void UpdateSelectionBorder(Point currentPoint)
    {
        var left = Math.Min(_selectionStart.X, currentPoint.X);
        var top = Math.Min(_selectionStart.Y, currentPoint.Y);
        var width = Math.Abs(currentPoint.X - _selectionStart.X);
        var height = Math.Abs(currentPoint.Y - _selectionStart.Y);

        Canvas.SetLeft(_selectionBorder, left);
        Canvas.SetTop(_selectionBorder, top);
        _selectionBorder.Width = width;
        _selectionBorder.Height = height;
    }

    private T GetRequiredElement<T>(string name) where T : FrameworkElement
    {
        return FindName(name) as T ??
            throw new InvalidOperationException($"The required XAML element '{name}' was not found.");
    }
}
