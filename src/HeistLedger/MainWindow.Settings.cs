using System.Windows;
using System.Windows.Controls;
using GTAEarningsTracker.Behaviors;
using Button = System.Windows.Controls.Button;

namespace GTAEarningsTracker;

public partial class MainWindow
{
    private async void OpenSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isCompactMode) await SetWindowModeAsync(false);
        await SelectTabAnimatedAsync("Settings");
        await SaveSettingsQuietlyAsync();
    }
    private void SettingsSectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name } || FindName(name) is not FrameworkElement section) return;
        var relative = section.TransformToAncestor(SettingsScrollViewer).Transform(new System.Windows.Point());
        SmoothScrollBehavior.ScrollTo(SettingsScrollViewer, SettingsScrollViewer.VerticalOffset + relative.Y - 10);
    }

    private void SetUpdateNotification(string? version)
    {
        // Keep a discovered update visible across launches, even when temporarily
        // offline. Clear it only after installing it or a successful fresh check.
        var available = Version.TryParse(version, out var latest) &&
            Version.TryParse(Services.AppUpdateService.CurrentVersion, out var current) && latest > current;
        _captureSettings.KnownUpdateVersion = available ? version : null;
        SettingsUpdateBadge.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        SettingsUpdateBadge.ToolTip = available ? $"Version {version} is available" : null;
        SettingsUpdateStateText.Text = available ? "!  UPDATE" : "VERSION";
    }
}
