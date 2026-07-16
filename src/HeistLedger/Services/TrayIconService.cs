using System.Drawing;

namespace GTAEarningsTracker.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.ToolStripMenuItem _statusItem;
    private bool _disposed;

    public TrayIconService()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        var showItem = new System.Windows.Forms.ToolStripMenuItem("Show Heist Ledger");
        showItem.Click += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
        _statusItem = new System.Windows.Forms.ToolStripMenuItem("Quick Scan: Select a payout area")
        {
            Enabled = false
        };
        var exitItem = new System.Windows.Forms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(showItem);
        menu.Items.Add(_statusItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = LoadApplicationIcon(),
            Text = "Heist Ledger — Quick Scan",
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
        _notifyIcon.BalloonTipClicked += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? ShowRequested;

    public event EventHandler? ExitRequested;

    public void UpdateScannerStatus(bool active, string status)
    {
        if (_disposed)
        {
            return;
        }

        _notifyIcon.Text = "Heist Ledger — Quick Scan";
        _statusItem.Text = $"Quick Scan: {status}";
    }

    public void ShowPayoutNotification(long amount, bool automaticallyAdded)
    {
        if (_disposed)
        {
            return;
        }

        _notifyIcon.BalloonTipTitle = automaticallyAdded ? "Heist Ledger" : "Payout detected";
        _notifyIcon.BalloonTipText = automaticallyAdded
            ? $"Added {amount.ToString("$#,##0", System.Globalization.CultureInfo.GetCultureInfo("en-US"))} to your earnings."
            : $"Open Heist Ledger to confirm {amount.ToString("$#,##0", System.Globalization.CultureInfo.GetCultureInfo("en-US"))}.";
        _notifyIcon.ShowBalloonTip(4000);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Icon?.Dispose();
        _notifyIcon.Dispose();
    }

    private static Icon LoadApplicationIcon()
    {
        var executablePath = Environment.ProcessPath;
        if (executablePath is not null)
        {
            using var extracted = Icon.ExtractAssociatedIcon(executablePath);
            if (extracted is not null)
            {
                return (Icon)extracted.Clone();
            }
        }

        return (Icon)SystemIcons.Application.Clone();
    }
}
