using System.Windows;
using MyLianPC.App.Services;

namespace MyLianPC.App;

public partial class App : Application
{
    private const string InstanceMutex = @"Local\MyLianPC.App";
    private const string ShowEvent = @"Local\MyLianPC.App.Show";

    private Mutex? _instance;
    private EventWaitHandle? _showSignal;
    private TrayIcon? _tray;
    private MainWindow? _window;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instance = new Mutex(true, InstanceMutex, out bool first);
        if (!first)
        {
            // Another instance runs in this Windows session: bring it to the front and exit.
            try { EventWaitHandle.OpenExisting(ShowEvent).Set(); } catch (WaitHandleCannotBeOpenedException) { }
            Shutdown();
            return;
        }
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEvent);
        new Thread(() => { while (_showSignal.WaitOne()) Dispatcher.Invoke(ShowWindow); }) { IsBackground = true }.Start();

        var state = AppState.Current;
        SystemIntegration.ApplyTheme(state.Preferences.Appearance);
        SystemIntegration.AdoptLegacyStartupEntry();
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "MyLianPC", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        string? Arg(string name) => e.Args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        if (Arg("--theme") is { } t && Enum.TryParse<Core.Settings.AppearanceMode>(t, true, out var forced)) SystemIntegration.ApplyTheme(forced);

        _tray = new TrayIcon(ShowWindow, ExitApp);
        _window = new MainWindow();
        MainWindow = _window;
        if (!e.Args.Contains("--tray")) _window.Show();
        if (Arg("--page") is { } page) _window.Navigate(page);
        await state.StartAsync();
        _window.UpdatePolling();

        // Developer aid: render the window to a PNG and exit (used for visual checks of both themes).
        if (Arg("--screenshot") is { } file)
        {
            await Task.Delay(2500);
            _window.SaveScreenshot(file);
            ExitApp();
        }
    }

    public void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void ExitApp()
    {
        _tray?.Dispose();
        _tray = null;
        _window?.CloseForReal();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
