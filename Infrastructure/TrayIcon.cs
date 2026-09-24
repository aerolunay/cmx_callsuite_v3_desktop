using System.Windows;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace CmxDialer.Infrastructure;

/// <summary>
/// Notification-area (tray) icon: handset on a white tile, a tooltip with the
/// agent's name and live status, left-click to bring the dialer forward, and a
/// right-click menu (Show dialer / Exit — Exit only works when signed out).
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int MaxTooltip = 63; // NotifyIcon limit on older Windows builds

    private readonly Forms.NotifyIcon _icon;
    private readonly Window _window;
    private readonly Func<string> _tooltip;
    private readonly DispatcherTimer _timer;

    public TrayIcon(Window window, Func<string> tooltip, Func<bool> canExit, Action exit)
    {
        _window = window;
        _tooltip = tooltip;

        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/handset.ico"))
                       ?? throw new InvalidOperationException("Tray icon resource missing.");
        using (var stream = resource.Stream)
        {
            _icon = new Forms.NotifyIcon
            {
                Icon = new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize),
                Text = "CMX CallSuite Desktop v3",
                Visible = true,
            };
        }

        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) BringToFront();
        };

        var showItem = new Forms.ToolStripMenuItem("Show dialer", null, (_, _) => BringToFront());
        var exitItem = new Forms.ToolStripMenuItem("Exit", null, (_, _) => exit());
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(showItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);
        menu.Opening += (_, _) =>
        {
            exitItem.Enabled = canExit();
            exitItem.ToolTipText = exitItem.Enabled ? "" : "Sign out first";
        };
        _icon.ContextMenuStrip = menu;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    private void Refresh()
    {
        var text = _tooltip();
        if (text.Length > MaxTooltip) text = text[..MaxTooltip];
        if (_icon.Text != text) _icon.Text = text;
    }

    private void BringToFront()
    {
        if (_window.WindowState != WindowState.Normal) _window.WindowState = WindowState.Normal;
        _window.Show();
        _window.Activate();
        // Toggle Topmost to force it above other windows even if pinning is off.
        var pinned = _window.Topmost;
        _window.Topmost = true;
        _window.Topmost = pinned;
    }

    public void Dispose()
    {
        _timer.Stop();
        _icon.Visible = false;
        _icon.Dispose();
    }
}
