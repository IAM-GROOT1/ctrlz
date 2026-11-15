using System.Windows;
using System.Windows.Threading;
using CtrlZ.App.Services;
using CtrlZ.App.ViewModels;
using CtrlZ.Core.Logging;

namespace CtrlZ.App;

public partial class App : Application
{
    private AppServices? _services;
    private ThemeService? _theme;
    private MainViewModel? _main;
    private bool _handlingFatal;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            _services = AppServices.Create();
            _theme = new ThemeService(this, _services.Log);
            _theme.Apply(_services.State.Settings.Theme);

            var autoInstall = e.Args.Any(arg => arg.Equals("--auto-install", StringComparison.OrdinalIgnoreCase));
            _main = new MainViewModel(_services, new DialogService(), _theme, autoInstall);
            var window = new MainWindow { DataContext = _main };
            MainWindow = window;
            window.Show();
            await _main.InitializeAsync();

            // Revalidate quietly. Never block startup or show a popup for license errors.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _services.License.InitializeAsync();
                }
                catch (Exception ex)
                {
                    _services.Log.Write(LogLevel.Warning, "License startup check failed.", ex);
                }
            });
        }
        catch (Exception ex)
        {
            _services?.Log.Write(LogLevel.Error, "Startup failed.", ex);
            MessageBox.Show(
                "CTRL+Z could not start.\n\n" + ex.Message +
                (_services is null ? string.Empty : $"\n\nLog: {_services.Log.LogFilePath}"),
                "CTRL+Z", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _main?.Dispose();
        _services?.Dispose();
        _theme?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _services?.Log.Write(LogLevel.Error, "Unhandled UI exception.", e.Exception);
        if (_handlingFatal)
        {
            return;
        }

        _handlingFatal = true;
        try
        {
            MessageBox.Show(
                "CTRL+Z hit an unexpected error, but your profile and install state are safe.\n\n" +
                e.Exception.Message + $"\n\nLog: {_services?.Log.LogFilePath}",
                "CTRL+Z", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _handlingFatal = false;
        }

        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        _services?.Log.Write(LogLevel.Error, "Unhandled application exception.",
            e.ExceptionObject as Exception);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _services?.Log.Write(LogLevel.Error, "Unobserved task exception.", e.Exception);
        e.SetObserved();
    }
}
