using System.Windows;
using System.Windows.Threading;
using Emulate.Services;

namespace Emulate;

public partial class App : Application
{
    private static Mutex? _singleInstance;

    /// <summary>Version passed by the updater after a successful update ("--updated 1.0.12").</summary>
    public static string? JustUpdatedTo { get; private set; }
    public static bool UpdateFailed { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Error("Unhandled", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { Log.Error("Unobserved task", a.Exception); a.SetObserved(); };

        var args = e.Args;
        var i = Array.IndexOf(args, "--updated");
        if (i >= 0) JustUpdatedTo = i + 1 < args.Length ? args[i + 1] : UpdateService.CurrentVersion.ToString();
        UpdateFailed = args.Contains("--update-failed");

        // The updater starts the new version right after the old one exits, so wait briefly for the mutex.
        _singleInstance = new Mutex(false, "Emulate.SingleInstance.v1");
        bool owned;
        try { owned = _singleInstance.WaitOne(JustUpdatedTo != null || UpdateFailed ? 15000 : 500); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            MessageBox.Show("Emulate is already running. Check your taskbar.", "Emulate", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Log.Info($"Emulate {UpdateService.CurrentVersion} starting from {AppPaths.InstallDir}");
        try { if (Directory.Exists(WebAccess.UploadDir)) Directory.Delete(WebAccess.UploadDir, true); } catch { }
        BridgeServer.Start();

        var main = new MainWindow();
        MainWindow = main;
        main.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _singleInstance?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }

    /// <summary>Exits immediately so the updater can replace the files. Running emulators keep running and are re-attached on restart.</summary>
    public static void ShutdownForUpdate()
    {
        Current.Dispatcher.Invoke(() =>
        {
            (Current.MainWindow as MainWindow)?.ReleaseEmbeddedWindows();
            try { _singleInstance?.ReleaseMutex(); } catch { }
            _singleInstance = null;
            Current.Shutdown();
        });
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI exception", e.Exception);
        MessageBox.Show(e.Exception.Message, "Emulate - something went wrong", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
