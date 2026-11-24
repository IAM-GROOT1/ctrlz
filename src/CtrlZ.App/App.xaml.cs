// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.App.Localization;
using System.Diagnostics;
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

    /// <summary>
    /// Written to the update result file in English, so the log stays English; the dialog shows
    /// the translated text for it (see UpdateCoordinator).
    /// </summary>
    internal const string UpdateRolledBackMessage =
        "The new version did not finish starting last time, so CTRL+Z went back to the previous version.";

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

            var probation = StartProbation(_services);
            if (probation is null)
            {
                // The previous build is back in place and has been started; this one steps aside.
                Shutdown(0);
                return;
            }

            _theme = new ThemeService(this, _services.Log);
            _theme.Apply(_services.State.Settings.Theme);

            var autoInstall = e.Args.Any(arg => arg.Equals("--auto-install", StringComparison.OrdinalIgnoreCase));
            _main = new MainViewModel(_services, new DialogService(), _theme, autoInstall);
            var window = new MainWindow { DataContext = _main };
            MainWindow = window;
            window.ContentRendered += (_, _) => probation();
            window.Show();
            await _main.InitializeAsync();
        }
        catch (Exception ex)
        {
            _services?.Log.Write(LogLevel.Error, "Startup failed.", ex);
            MessageBox.Show(
                Loc.Get("App_CTRLZCouldNotStart") + ex.Message +
                (_services is null ? string.Empty : Loc.Format("App_Log", _services.Log.LogFilePath)),
                "CTRL+Z", MessageBoxButton.OK, MessageBoxImage.Error, MessageBoxResult.OK, AppLanguage.MessageBoxOptions);
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

    /// <summary>
    /// Checks a portable update that is still on probation. Returns the action to run once the
    /// main window has shown, or null when the last start of this build never showed its window
    /// and the previous build was put back and started instead.
    /// </summary>
    private static Action? StartProbation(AppServices services)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            return () => { };
        }

        try
        {
            UpdateProbation.RemoveFailedBuild(exe);
            var probation = new UpdateProbation(Path.Combine(services.Paths.State, UpdateProbation.FileName), services.Log);
            using var current = Process.GetCurrentProcess();
            var processId = current.Id;

            switch (probation.OnLaunch(exe, processId, current.StartTime.ToUniversalTime()))
            {
                case ProbationStartup.RolledBack:
                    UpdateApplyFiles.WriteResult(
                        Path.Combine(services.Paths.State, UpdateApplyFiles.ResultFileName),
                        UpdateApplyResult.Failed(
                            UpdateRolledBackMessage));
                    if (UpdateApplier.TryStart(exe) is { } restarted)
                    {
                        restarted.Dispose();
                        return null;
                    }

                    // The restored build could not be started from here; carry on in this one.
                    return () => { };

                case ProbationStartup.Watching:
                    return () =>
                    {
                        try
                        {
                            probation.MarkLoaded(exe, processId);
                        }
                        catch (Exception ex)
                        {
                            services.Log.Write(LogLevel.Warning, "Could not record that the main window loaded.", ex);
                        }
                    };

                default:
                    return () => { };
            }
        }
        catch (Exception ex)
        {
            // Never block a start over the probation check.
            services.Log.Write(LogLevel.Warning, "The update probation check failed.", ex);
            return () => { };
        }
    }

    private async Task RunApplyUpdateAsync(string requestPath)
    {
        // Read first: the helper runs from a temp copy, so the data folders (and portable.flag)
        // belong to the app being updated, not to this exe.
        var request = UpdateApplyFiles.ReadRequest(requestPath);

        AppPaths paths;
        FileAppLog log;
        try
        {
            paths = string.IsNullOrWhiteSpace(request?.TargetExe)
                ? AppPaths.CreateDefault()
                : AppPaths.CreateFor(request.TargetExe);
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

            if (request is null)
            {
                log.Write(LogLevel.Error, $"The update helper could not read its request ({requestPath}).");
                return;
            }

            var probation = new UpdateProbation(Path.Combine(paths.State, UpdateProbation.FileName), log);
            var result = await new UpdateApplier(log, probation).ApplyAsync(request).ConfigureAwait(true);

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
                Loc.Get("App_CTRLZHitAnUnexpected") +
                e.Exception.Message +
                (_services is null ? string.Empty : Loc.Format("App_Log", _services.Log.LogFilePath)),
                "CTRL+Z", MessageBoxButton.OK, MessageBoxImage.Warning, MessageBoxResult.OK, AppLanguage.MessageBoxOptions);
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
