using Avalonia;
using VictoryTool.Application;
using VictoryTool.Application.Diagnostics;

namespace VictoryTool.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        GlobalLog.StartSession(ApplicationVersion.Current);
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            GlobalLog.Info("desktop_lifetime_starting");
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            GlobalLog.Error("application_crashed", exception);
            throw;
        }
        finally
        {
            GlobalLog.Shutdown();
        }
    }

    private static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs args) =>
        GlobalLog.Error(
            "unhandled_exception",
            args.ExceptionObject as Exception,
            new Dictionary<string, object?>
            {
                ["isTerminating"] = args.IsTerminating,
            });

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args) =>
        GlobalLog.Error("unobserved_task_exception", args.Exception);

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder
            .Configure<App>()
            .UsePlatformDetect();
    }
}
