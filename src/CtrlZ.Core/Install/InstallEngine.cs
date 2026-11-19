// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Text;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Models;
using CtrlZ.Core.Services;

namespace CtrlZ.Core.Install;

/// <summary>
/// Sequential, fault-isolated install runner. Every state transition is persisted, so a
/// crash, failed installer, or reboot never loses the position in the queue.
/// </summary>
public sealed class InstallEngine
{
    private readonly AppPaths _paths;
    private readonly IReadOnlyList<ISourceInstaller> _installers;
    private readonly InstallStateStore _stateStore;
    private readonly ProfileStore _profiles;
    private readonly WingetBootstrapService _wingetBootstrap;
    private readonly IAppLog _log;

    public InstallEngine(
        AppPaths paths,
        IReadOnlyList<ISourceInstaller> installers,
        InstallStateStore stateStore,
        ProfileStore profiles,
        WingetBootstrapService wingetBootstrap,
        IAppLog log)
    {
        _paths = paths;
        _installers = installers;
        _stateStore = stateStore;
        _profiles = profiles;
        _wingetBootstrap = wingetBootstrap;
        _log = log;
    }

    public InstallState CreateState(AppProfile profile, string? profilePath)
    {
        var state = new InstallState
        {
            ProfileId = profile.Id,
            ProfileName = profile.Name,
            ProfilePath = profilePath,
            ProfileSnapshot = profile.Clone(),
            Items = profile.Apps
                .Where(app => app.Enabled)
                .Select(app => new InstallItemState
                {
                    AppId = app.Id,
                    Name = app.Name,
                    Source = app.Source,
                    Status = InstallStatus.Queued
                })
                .ToList()
        };
        _stateStore.Save(state);
        _log.Write(LogLevel.Info, $"Created install session for '{profile.Name}' with {state.Items.Count} apps.");
        return state;
    }

    public InstallState? LoadSavedState() => _stateStore.Load();

    public InstallState? LoadResumableState()
    {
        var state = _stateStore.Load();
        return state is not null && state.HasUnfinishedWork ? state : null;
    }

    public async Task<InstallState> RunAsync(
        InstallState state,
        IInstallLogSink? sink = null,
        bool retryFailed = false,
        CancellationToken cancellationToken = default)
    {
        sink ??= NullInstallLogSink.Instance;
        var profile = state.ProfileSnapshot;
        if (profile is null && !string.IsNullOrWhiteSpace(state.ProfilePath) && File.Exists(state.ProfilePath))
        {
            profile = _profiles.Load(state.ProfilePath);
            state.ProfileSnapshot = profile;
        }

        if (profile is null)
        {
            throw new InvalidOperationException(
                "The saved profile for this install session is missing. Start a new install from My Apps.");
        }

        using var sessionLog = new SessionLog(_paths, state, _log, sink);
        sessionLog.Line($"CTRL+Z install session · profile '{profile.Name}' · {state.Items.Count} apps");
        sessionLog.Line($"Running {(ElevationService.IsElevated ? "elevated" : "without elevation")} on {Environment.MachineName}.");

        ReconcileItems(state, profile);
        if (retryFailed)
        {
            foreach (var item in state.Items.Where(i => i.Status is InstallStatus.Failed or InstallStatus.NeedsManualAction))
            {
                item.Status = InstallStatus.Queued;
                item.Error = null;
                item.Detail = null;
            }
        }

        foreach (var item in state.Items.Where(i => i.Status is InstallStatus.Preparing or InstallStatus.Downloading or InstallStatus.Installing or InstallStatus.Cancelled))
        {
            item.Status = InstallStatus.Queued;
            item.Error = null;
            item.Detail = null;
        }

        state.IsFinished = false;
        state.LastError = null;
        state.UpdatedUtc = DateTimeOffset.UtcNow;
        _stateStore.Save(state);

        var pendingWinget = state.Items.Any(i =>
            i.Status == InstallStatus.Queued && i.Source is AppSource.Winget or AppSource.MicrosoftStore);
        if (pendingWinget)
        {
            sessionLog.Line("Checking winget availability…");
            try
            {
                await _wingetBootstrap.EnsureReadyAsync(sessionLog.Line, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                sessionLog.Line("winget is not ready: " + ex.Message);
                _log.Write(LogLevel.Warning, "winget bootstrap failed; winget apps may need manual action.", ex);
            }
        }

        var total = state.Items.Count;
        var index = 0;
        foreach (var item in state.Items)
        {
            index++;
            if (item.Status is InstallStatus.Success or InstallStatus.Skipped or InstallStatus.RebootRequired)
            {
                sessionLog.Report(item, InstallStatus.Success, item.Detail ?? "Already complete.", 1);
                continue;
            }

            if (item.Status is InstallStatus.Failed or InstallStatus.NeedsManualAction)
            {
                sessionLog.Line($"Skipping failed app '{item.Name}' (use Retry failed to try again).");
                continue;
            }

            var app = profile.Apps.FirstOrDefault(a => a.Id == item.AppId);
            if (app is null || !app.Enabled)
            {
                item.Status = InstallStatus.Skipped;
                item.Detail = "Removed from the profile.";
                _stateStore.Save(state);
                continue;
            }

            var installer = _installers.FirstOrDefault(i => i.CanHandle(app.Source));
            if (installer is null)
            {
                item.Status = InstallStatus.NeedsManualAction;
                item.Error = $"No installer is available for {app.Source}.";
                item.UpdatedUtc = DateTimeOffset.UtcNow;
                _stateStore.Save(state);
                sessionLog.Line($"[{item.Name}] {item.Error}");
                continue;
            }

            item.Status = InstallStatus.Preparing;
            item.Attempts++;
            item.Error = null;
            item.Detail = $"Starting ({index}/{total})…";
            item.UpdatedUtc = DateTimeOffset.UtcNow;
            _stateStore.Save(state);
            sessionLog.Line($"[{index}/{total}] {app.Name} — {app.Source.Display()}");
            sessionLog.Report(item, InstallStatus.Preparing, item.Detail, 0);

            var adapter = new ItemSink(sessionLog, sink, item);
            var context = new InstallContext(_paths, adapter, _log, ElevationService.IsElevated);
            try
            {
                var result = await installer.InstallAsync(app, context, cancellationToken).ConfigureAwait(false);
                item.ExitCode = result.ExitCode;
                item.UpdatedUtc = DateTimeOffset.UtcNow;
                if (result.Status == InstallStatus.RebootRequired)
                {
                    item.Status = InstallStatus.RebootRequired;
                    item.Detail = result.Detail ?? "Restart required.";
                    state.RebootRequested = true;
                    sessionLog.Line($"[{item.Name}] installed; a restart is required.");
                }
                else if (result.Success)
                {
                    item.Status = InstallStatus.Success;
                    item.Detail = result.Detail;
                    sessionLog.Line($"[{item.Name}] success.");
                }
                else if (result.Status == InstallStatus.NeedsManualAction)
                {
                    item.Status = InstallStatus.NeedsManualAction;
                    item.Error = result.Error ?? result.Detail;
                    item.Detail = result.Detail;
                    sessionLog.Line($"[{item.Name}] needs manual action: {item.Error}");
                }
                else
                {
                    item.Status = InstallStatus.Failed;
                    item.Error = result.Error;
                    item.Detail = result.Detail;
                    sessionLog.Line($"[{item.Name}] failed: {item.Error}");
                }
            }
            catch (OperationCanceledException)
            {
                item.Status = InstallStatus.Queued;
                item.Detail = "Paused.";
                item.UpdatedUtc = DateTimeOffset.UtcNow;
                _stateStore.Save(state);
                sessionLog.Line($"[{item.Name}] installation paused.");
                throw;
            }
            catch (Exception ex)
            {
                item.Status = InstallStatus.Failed;
                item.Error = ex.Message;
                item.UpdatedUtc = DateTimeOffset.UtcNow;
                _log.Write(LogLevel.Error, $"Unexpected failure installing {app.Name}.", ex);
                sessionLog.Line($"[{item.Name}] unexpected failure: {ex.Message}");
            }

            _stateStore.Save(state);
            sessionLog.Report(item, item.Status, item.Detail, item.Status == InstallStatus.Success ? 1 : null);
        }

        state.IsFinished = true;
        state.LastError = state.FailedCount > 0
            ? $"{state.FailedCount} app{(state.FailedCount == 1 ? string.Empty : "s")} need attention."
            : null;
        _stateStore.Save(state);

        sessionLog.Line(string.Empty);
        sessionLog.Line($"Finished: {state.CompletedCount} complete, {state.FailedCount} need attention.");
        if (state.RebootRequested)
        {
            sessionLog.Line("One or more installers requested a restart. Restart Windows when convenient.");
        }

        return state;
    }

    private void ReconcileItems(InstallState state, AppProfile profile)
    {
        var existing = state.Items.Select(i => i.AppId).ToHashSet(StringComparer.Ordinal);
        foreach (var app in profile.Apps.Where(a => a.Enabled && !existing.Contains(a.Id)))
        {
            state.Items.Add(new InstallItemState
            {
                AppId = app.Id,
                Name = app.Name,
                Source = app.Source,
                Status = InstallStatus.Queued
            });
        }

        foreach (var item in state.Items)
        {
            var app = profile.Apps.FirstOrDefault(a => a.Id == item.AppId);
            if (app is null || !app.Enabled)
            {
                if (item.Status is InstallStatus.Queued or InstallStatus.Preparing)
                {
                    item.Status = InstallStatus.Skipped;
                    item.Detail = "Not in the active profile.";
                }
            }
            else
            {
                item.Name = app.Name;
                item.Source = app.Source;
            }
        }
    }

    private sealed class ItemSink : IInstallLogSink
    {
        private readonly SessionLog _session;
        private readonly IInstallLogSink _ui;
        private readonly InstallItemState _item;
        private InstallStatus _lastStatus = InstallStatus.Preparing;

        public ItemSink(SessionLog session, IInstallLogSink ui, InstallItemState item)
        {
            _session = session;
            _ui = ui;
            _item = item;
        }

        public void Line(string message) => _session.Line(message);

        public void Progress(InstallProgress progress)
        {
            if (progress.Status != _lastStatus)
            {
                _lastStatus = progress.Status;
                _item.Status = progress.Status;
            }

            if (!string.IsNullOrWhiteSpace(progress.Detail))
            {
                _item.Detail = progress.Detail;
            }

            _item.UpdatedUtc = DateTimeOffset.UtcNow;
            _session.Report(_item, progress.Status, progress.Detail, progress.Fraction);
        }
    }

    private sealed class SessionLog : IDisposable
    {
        private readonly object _gate = new();
        private readonly string _path;
        private readonly IInstallLogSink _sink;
        private readonly IAppLog _log;

        public SessionLog(AppPaths paths, InstallState state, IAppLog log, IInstallLogSink sink)
        {
            _sink = sink;
            _log = log;
            if (string.IsNullOrWhiteSpace(state.LogPath))
            {
                state.LogPath = CreateLogPath(paths);
            }

            _path = state.LogPath!;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        }

        public void Line(string message)
        {
            try
            {
                lock (_gate)
                {
                    File.AppendAllText(_path,
                        $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}",
                        Encoding.UTF8);
                }
            }
            catch
            {
                // A full disk must not stop an installer queue.
            }

            try { _sink.Line(message); } catch { }
        }

        public void Report(InstallItemState item, InstallStatus status, string? detail, double? fraction)
        {
            try
            {
                _sink.Progress(new InstallProgress(item.Name, status, detail, fraction));
            }
            catch
            {
                // UI failures do not affect install state.
            }
        }

        public void Dispose()
        {
            try { _log.Write(LogLevel.Info, $"Install log written to {_path}."); } catch { }
        }

        private static string CreateLogPath(AppPaths paths)
        {
            Directory.CreateDirectory(paths.Logs);
            var basePath = Path.Combine(paths.Logs, $"install-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            if (!File.Exists(basePath))
            {
                return basePath;
            }

            return Path.Combine(paths.Logs,
                $"install-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("n")[..4]}.log");
        }
    }
}
