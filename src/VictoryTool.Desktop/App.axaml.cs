using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using VictoryTool.Application.Diagnostics;
using VictoryTool.Desktop.ViewModels;
using VictoryTool.Desktop.Views;

namespace VictoryTool.Desktop;

public sealed partial class App : Avalonia.Application
{
    public override void Initialize()
    {
        GlobalLog.Info("avalonia_initializing");
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Dispatcher.UIThread.UnhandledException += OnUiUnhandledException;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            GlobalLog.Info("desktop_lifetime_initialized", new Dictionary<string, object?>
            {
                ["lifetime"] = nameof(IClassicDesktopStyleApplicationLifetime),
            });
            var viewModel = MainWindowViewModel.CreateDesktopDefault();
            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel,
            };
            _ = viewModel.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void OnUiUnhandledException(
        object? sender,
        DispatcherUnhandledExceptionEventArgs args) =>
        GlobalLog.Error("ui_unhandled_exception", args.Exception);
}
