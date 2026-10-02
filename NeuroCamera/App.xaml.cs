using System.Windows;
using System.Windows.Threading;
using NeuroCamera.Common;

namespace NeuroCamera;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Two independent nets: DispatcherUnhandledException catches exceptions that escape
        // from UI-thread event handlers (the overwhelming majority in a WPF app);
        // AppDomain.UnhandledException additionally catches anything that escapes a background
        // thread NeuroCamera itself doesn't fully control (a stray exception from a library's
        // own worker thread, for instance) - by the time that one fires the process is already
        // terminating, so it only has the chance to log, never to keep the app alive.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
    }

    // Belt-and-braces process exit: WPF's own shutdown (ShutdownMode="OnMainWindowClose")
    // closes every window and ends the Dispatcher message loop, which is normally enough for
    // the process to exit on its own once Main() returns. In practice this app also opens
    // native/COM resources outside the CLR's control - DirectShow camera filters via COM RCWs
    // (HardwareCameraController), OpenCvSharp's native VideoCapture/Mat handles, and a
    // dedicated capture thread. If any of those is momentarily slow to let go (a COM object
    // still being finalized, a native call not yet returned), the managed runtime can be left
    // waiting and the process lingers in Task Manager after the window has visually closed.
    // Forcing the exit here removes that dependency entirely: once WPF's own shutdown sequence
    // has run (so MainWindow.Closing/Closed had their chance to release resources cleanly),
    // the process is guaranteed to actually end.
    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
        Environment.Exit(e.ApplicationExitCode);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        CrashLog.Write("UI thread (DispatcherUnhandledException)", args.Exception);

        MessageBox.Show(
            $"Непредвиденная ошибка: {args.Exception.Message}\n\nПодробности сохранены в {CrashLog.DefaultPath}",
            "NeuroCamera",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        args.Handled = true;
    }

    private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception exception)
        {
            CrashLog.Write("Unhandled background-thread exception (process terminating)", exception);
        }
    }
}
