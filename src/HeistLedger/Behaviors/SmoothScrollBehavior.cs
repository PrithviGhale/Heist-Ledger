using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace GTAEarningsTracker.Behaviors;

public static class SmoothScrollBehavior
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SmoothScrollBehavior), new PropertyMetadata(false, OnEnabledChanged));
    private static readonly DependencyProperty ControllerProperty = DependencyProperty.RegisterAttached(
        "Controller", typeof(ScrollController), typeof(SmoothScrollBehavior));
    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not ScrollViewer viewer) return;
        if (viewer.GetValue(ControllerProperty) is ScrollController old) old.Detach();
        viewer.SetValue(ControllerProperty, (bool)args.NewValue ? new ScrollController(viewer) : null);
    }

    private sealed class ScrollController : Animatable
    {
        private readonly ScrollViewer _viewer;
        private double _target;
        private bool _animating;
        private static readonly DependencyProperty OffsetProperty = DependencyProperty.Register(
            "Offset", typeof(double), typeof(ScrollController), new PropertyMetadata(0d,
                (sender, args) => ((ScrollController)sender)._viewer.ScrollToVerticalOffset((double)args.NewValue)));
        public ScrollController(ScrollViewer viewer)
        {
            _viewer = viewer;
            viewer.PreviewMouseWheel += OnWheel;
            viewer.PreviewMouseDown += OnMouseDown;
            viewer.PreviewKeyDown += OnKeyDown;
            viewer.Unloaded += OnUnloaded;
            viewer.IsVisibleChanged += OnVisibilityChanged;
        }
        protected override Freezable CreateInstanceCore() => throw new NotSupportedException("Scroll controllers are not cloned.");
        private void OnWheel(object sender, MouseWheelEventArgs args)
        {
            if (args.Handled || _viewer.ScrollableHeight <= 0 || SystemParameters.WheelScrollLines == 0) return;
            // Nested notes/text inputs keep their native wheel handling.
            for (var node = args.OriginalSource as DependencyObject; node is not null && node != _viewer;
                 node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            {
                if (node is ScrollViewer or ComboBox or TextBox) return;
            }
            var step = SystemParameters.WheelScrollLines < 0 ? _viewer.ViewportHeight * 0.85 : SystemParameters.WheelScrollLines * 18d;
            var target = Math.Clamp((_animating ? _target : _viewer.VerticalOffset) - args.Delta / 120d * step, 0, _viewer.ScrollableHeight);
            ScrollTo(target);
            args.Handled = true;
        }
        public void ScrollTo(double target)
        {
            var start = _viewer.VerticalOffset;
            Stop();
            _target = Math.Clamp(target, 0, _viewer.ScrollableHeight);
            if (!SystemParameters.ClientAreaAnimation || Math.Abs(start - _target) < 1)
            {
                _viewer.ScrollToVerticalOffset(_target);
                return;
            }
            SetValue(OffsetProperty, start);
            _animating = true;
            var animation = new DoubleAnimation(start, _target, TimeSpan.FromMilliseconds(170))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Timeline.SetDesiredFrameRate(animation, 60);
            animation.Completed += (_, _) => Stop();
            BeginAnimation(OffsetProperty, animation);
        }
        private void Stop()
        {
            var current = _viewer.VerticalOffset;
            SetValue(OffsetProperty, current);
            BeginAnimation(OffsetProperty, null);
            _animating = false;
        }
        private void OnMouseDown(object sender, MouseButtonEventArgs args) => Stop();
        private void OnKeyDown(object sender, KeyEventArgs args) => Stop();
        private void OnUnloaded(object sender, RoutedEventArgs args) => Stop();
        private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) { if (!_viewer.IsVisible) Stop(); }
        public void Detach()
        {
            Stop();
            _viewer.PreviewMouseWheel -= OnWheel;
            _viewer.PreviewMouseDown -= OnMouseDown;
            _viewer.PreviewKeyDown -= OnKeyDown;
            _viewer.Unloaded -= OnUnloaded;
            _viewer.IsVisibleChanged -= OnVisibilityChanged;
        }
    }

    public static void ScrollTo(ScrollViewer viewer, double offset)
    {
        if (viewer.GetValue(ControllerProperty) is ScrollController controller) controller.ScrollTo(offset);
        else viewer.ScrollToVerticalOffset(offset);
    }
}
