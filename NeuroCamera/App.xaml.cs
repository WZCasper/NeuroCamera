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
