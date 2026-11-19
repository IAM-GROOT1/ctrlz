// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.IO;
using System.Windows;
using System.Windows.Threading;
using CtrlZ.App.Services;
using CtrlZ.App.ViewModels;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Updates;

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
            // The update helper is a copy of this exe. It must never build the UI: it waits
            // for the old process to exit, applies the update and restarts the app.
            var applyIndex = Array.FindIndex(e.Args, arg =>
                arg.Equals("--apply-update", StringComparison.OrdinalIgnoreCase));
            if (applyIndex >= 0 && applyIndex + 1 < e.Args.Length)
            {
                await RunApplyUpdateAsync(e.Args[applyIndex + 1]);
                Shutdown(0);
                return;
            }

            _services = AppServices.Create();
            _theme = new ThemeService(this, _services.Log);
            _theme.Apply(_services.State.Settings.Theme);

            var autoInstall = e.Args.Any(arg => arg.Equals("--auto-install", StringComparison.OrdinalIgnoreCase));
            _main = new MainViewModel(_services, new DialogService(), _theme, autoInstall);
            var window = new MainWindow { DataContext = _main };
            MainWindow = window;
            window.Show();
            await _main.InitializeAsync();
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

    private async Task RunApplyUpdateAsync(string requestPath)
    {
        AppPaths paths;
        FileAppLog log;
        try
        {
            paths = AppPaths.CreateDefault();
            paths.EnsureCreated();
            log = new FileAppLog(paths.Logs);
        }
        catch
        {
            // Nothing to log to; the helper cannot do useful work either.
            return;
        }

        try
        {
            log.Write(LogLevel.Info, "Update helper started.");

            var request = UpdateApplyFiles.ReadRequest(requestPath);
            if (request is null)
            {
                log.Write(LogLevel.Error, $"The update helper could not read its request ({requestPath}).");
                return;
            }

            var result = await new UpdateApplier(log).ApplyAsync(request).ConfigureAwait(true);

            UpdateApplyFiles.WriteResult(
                Path.Combine(paths.State, UpdateApplyFiles.ResultFileName),
                result);

            log.Write(
                result.Success ? LogLevel.Info : LogLevel.Error,
                result.Success
                    ? $"Update to {result.Version} applied."
                    : $"The update did not apply: {result.Message}");

            try
            {
                File.Delete(requestPath);
            }
            catch
            {
                // The temp folder is disposable.
            }
        }
        finally
        {
            log.Dispose();
        }
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
