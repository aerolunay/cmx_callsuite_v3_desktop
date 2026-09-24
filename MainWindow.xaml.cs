using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using CmxDialer.Infrastructure;
using CmxDialer.ViewModels;

namespace CmxDialer;

/// <summary>
/// Fixed 400×720 window. WindowStyle=None + ResizeMode=NoResize means Windows gives it no
/// minimize/maximize, and clicking the taskbar button doesn't minimize it either. While a
/// session is active the window refuses to close (Alt+F4 included) — agents sign out first.
/// </summary>
public partial class MainWindow : Window
{
    private TrayIcon? _tray;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closing += OnClosing;
        Loaded += (_, _) =>
        {
            FitToScreen();
            _tray ??= new TrayIcon(this, TrayText, () => Vm?.CanCloseWindow ?? true, Close);
        };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(new Action(FitToScreen));
        SizeChanged += (_, _) => FitToScreen(keepOnScreen: false);
        LocationChanged += (_, _) => FitToScreen(keepOnScreen: false); // moved to another monitor: adapt height only
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Closed += (_, _) => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        Closed += (_, _) => _tray?.Dispose();
        StateChanged += (_, _) =>
        {
            // Win+Down / "Show desktop" etc. can still try — always come back.
            if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private const double DesignWidth = 400;
    private const double DesignHeight = 720;
    private const double MinUsableHeight = 600;

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(FitToScreen));

    /// <summary>
    /// The window is designed at 400x720 (DIPs). On a 1080p laptop at 150% scaling the usable
    /// screen is only ~670 DIPs tall, so cap the height to the current monitor's work area and
    /// keep the window fully on screen. The disposition list simply gets a little shorter.
    /// </summary>
    private void FitToScreen() => FitToScreen(keepOnScreen: true);

    private void FitToScreen(bool keepOnScreen)
    {
        // Real usable height of this monitor in DIPs (the WPF units Height/Top use).
        var work = NativeMethods.GetWorkAreaDips(this) ?? SystemParameters.WorkArea;

        // Only shrink when the screen is genuinely shorter than the design, and never
        // below a height the dialer can still be used at.
        // Always restore the design width: with mixed-DPI monitors (e.g. a 200% laptop + a
        // 100% external screen) Windows can hand WPF a doubled or halved size on a DPI change.
        if (Math.Abs(Width - DesignWidth) > 0.5) Width = DesignWidth;

        var height = Math.Clamp(work.Height, MinUsableHeight, DesignHeight);
        if (Math.Abs(Height - height) > 0.5) Height = height;
        if (!keepOnScreen) return;
        if (Top + Height > work.Bottom) Top = Math.Max(work.Top, work.Bottom - Height);
        if (Top < work.Top) Top = work.Top;
    }

    private string TrayText()
    {
        var vm = Vm;
        if (vm == null || !vm.HasAgent) return "CallSuite v3 — signed out";
        var status = (vm.CurrentView as DialerViewModel)?.StatusLabel;
        return string.IsNullOrEmpty(status) ? $"CallSuite v3 — {vm.AgentName}" : $"CallSuite v3 — {vm.AgentName} · {status}";
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel old)
        {
            old.RequestClose -= OnRequestClose;
            old.RequestAttention -= OnRequestAttention;
        }
        if (e.NewValue is MainViewModel vm)
        {
            vm.RequestClose += OnRequestClose;
            vm.RequestAttention += OnRequestAttention;
        }
    }

    private void OnRequestClose()
    {
        Close();
    }

    private void OnRequestAttention()
    {
        if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        if (!IsActive)
        {
            NativeMethods.Flash(this);
            Activate();
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        var vm = Vm;
        if (vm != null && !vm.CanCloseWindow)
        {
            e.Cancel = true;
            MessageBox.Show(this, "Sign out before closing the dialer.", "CMX CallSuite Desktop v3",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        vm?.Shutdown();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}