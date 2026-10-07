using System.Net.Sockets;
using System.Windows;

namespace WolfSpeak;

public partial class App : Application
{
    const string InstanceMutexName = "WolfSpeak.SingleInstance";
    const string ShowEventName = "WolfSpeak.ShowWindow";

    VoiceEngine? engine;
    Mutex? instanceMutex;
    EventWaitHandle? showSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Safety net: an unexpected error is logged and shown, never fatal for the UI thread.
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write("Unhandled UI error", args.Exception);
            args.Handled = true;
            (MainWindow as MainWindow)?.ShowError("Something went wrong — WolfSpeak kept running. Details: " + Log.FilePath);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Write("Fatal background error", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Write("Unobserved task error", args.Exception);
            args.SetObserved();
        };

        // Already running (probably in the tray)? Ask that instance to show itself and leave.
        instanceMutex = new Mutex(true, InstanceMutexName, out bool firstInstance);
        if (!firstInstance)
        {
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            instanceMutex.Dispose();
            instanceMutex = null;
            Shutdown();
            return;
        }

        try
        {
            engine = new VoiceEngine();
        }
        catch (SocketException ex)
        {
            MessageBox.Show(
                $"Could not open UDP port {VoiceEngine.Port}. Is another app using it?\n\n{ex.Message}",
                "WolfSpeak", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        var window = new MainWindow(engine);
        if (e.Args.Contains(Autostart.TrayArgument))
            new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle(); // start hidden in the tray (hotkeys still work)
        else
            window.Show();
        Autostart.Refresh();

        showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (showSignal.WaitOne())
                Dispatcher.BeginInvoke(window.ShowFromTray);
        }) { IsBackground = true, Name = "WolfSpeak single-instance" }.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        engine?.Dispose();
        instanceMutex?.ReleaseMutex();
        instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
