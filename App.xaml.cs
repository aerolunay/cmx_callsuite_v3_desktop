using System.Windows;
using CmxDialer.Config;
using CmxDialer.Infrastructure;
using CmxDialer.ViewModels;

namespace CmxDialer;

public partial class App : Application
{
    // Two copies would register the same extension twice and fight over calls.
    private static Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, "CmxDialer.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("CMX CallSuite Desktop v3 is already running.", "CMX CallSuite Desktop v3", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception", args.Exception);
            MessageBox.Show(args.Exception.Message, "CMX CallSuite Desktop v3", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        var settings = AppSettings.Load();
        Log.Info($"Starting CMX CallSuite Desktop v3 — server {settings.ServerUrl}, SIP UDP port {settings.SipPort}");

        var window = new MainWindow { DataContext = new MainViewModel(settings) };
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.ReleaseMutex();
        base.OnExit(e);
    }
}
