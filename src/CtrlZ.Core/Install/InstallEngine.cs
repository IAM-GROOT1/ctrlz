// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Localization;
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
public sealed partial class InstallEngine
{
    private readonly AppPaths _paths;
    private readonly IReadOnlyList<ISourceInstaller> _installers;
    private readonly InstallStateStore _stateStore;
    private readonly ProfileStore _profiles;
    private readonly WingetBootstrapService _wingetBootstrap;
    private readonly IAppLog _log;
    private readonly DependencyCatalog _catalog;

    public InstallEngine(
        AppPaths paths,
        IReadOnlyList<ISourceInstaller> installers,
        InstallStateStore stateStore,
        ProfileStore profiles,
        WingetBootstrapService wingetBootstrap,
        IAppLog log,
        DependencyCatalog? catalog = null)
    {
        _catalog = catalog ?? DependencyCatalog.Empty;
        _paths = paths;
        _installers = installers;
        _stateStore = stateStore;
        _profiles = profiles;
        _wingetBootstrap = wingetBootstrap;
        _log = log;
    }

    /// <summary>
    /// How sessions saved before <see cref="InstallItemState.NotAttempted"/> existed marked an app that
    /// was not attempted. Only read, to recognise those sessions; new ones set the flag.
    /// </summary>
    public const string LegacyNotAttemptedPrefix = "Not attempted: ";

    /// <summary>The dependency catalog the engine plans with; the preview must use the same one.</summary>
    public DependencyCatalog Catalog => _catalog;

    /// <summary>The order and dependencies the engine uses; the preview builds the same plan.</summary>
    public InstallOrderResult PlanOrder(AppProfile profile) => new InstallOrderPlanner(_catalog).Plan(profile);

    public InstallState CreateState(AppProfile profile, string? profilePath)
    {
        var plan = PlanOrder(profile);

        // The snapshot carries any dependency the planner added, so the run can find it by id.
        var snapshot = profile.Clone();
        foreach (var added in plan.AutoAdded.Where(added => snapshot.Apps.All(app => app.Id != added.Id)))
        {
            snapshot.Apps.Insert(0, added.Clone());
        }

        var items = plan.Ordered
            .Select(app => new InstallItemState
            {
                AppId = app.Id,
                Name = app.Name,
                Source = app.Source,
                Status = InstallStatus.Queued
            })
            .ToList();

        // Apps the planner cannot place (install-order loop, or needing one) are listed as needing
        // attention with the reason, never silently dropped.
        foreach (var skipped in plan.Skipped)
        {
            var app = snapshot.Apps.FirstOrDefault(candidate => candidate.Id == skipped.AppId);
            items.Add(new InstallItemState
            {
                AppId = skipped.AppId,
                Name = skipped.Name,
                Source = app?.Source ?? AppSource.Winget,
                Status = InstallStatus.Failed,
                Error = CoreText.Format("InstallEngine_NotAttempted", skipped.Reason),
                Detail = CoreText.Format("InstallEngine_NotAttempted", skipped.Reason),
                NotAttempted = true
            });
        }

        var state = new InstallState
        {
            ProfileId = profile.Id,
            ProfileName = profile.Name,
            ProfilePath = profilePath,
            ProfileSnapshot = snapshot,
            Items = items
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
                CoreText.Get("InstallEngine_ProfileMissing"));
        }

        using var sessionLog = new SessionLog(_paths, state, _log, sink);
        sessionLog.Line(CoreText.Format("InstallEngine_CTRLZInstallSessionProfile", profile.Name, state.Items.Count));
        sessionLog.Line(ElevationService.IsElevated ? CoreText.Get("InstallEngine_RunningElevated") : CoreText.Get("InstallEngine_RunningNotElevated"));

        ReconcileItems(state, profile);
        if (retryFailed)
        {
            foreach (var item in state.Items.Where(i => i.Status is InstallStatus.Failed or InstallStatus.NeedsManualAction))
            {
                item.Status = InstallStatus.Queued;
                item.Error = null;
                item.Detail = null;
                item.NotAttempted = false;
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
            sessionLog.Line(CoreText.Get("InstallEngine_CheckingWingetAvailability"));
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
                sessionLog.Line(CoreText.Get("InstallEngine_WingetIsNotReady") + ex.Message);
                _log.Write(LogLevel.Warning, "winget bootstrap failed; winget apps may need manual action.", ex);
            }
        }

        var plan = PlanOrder(profile);
        var loopReasons = plan.Skipped.ToDictionary(skipped => skipped.AppId, skipped => skipped.Reason, StringComparer.Ordinal);

        // A resumed or reconciled session may hold items in an older order; run them in plan order
        // so a dependency always comes before the apps that need it. The sort is stable.
        var rank = plan.Ordered
            .Select((app, position) => (app.Id, position))
            .ToDictionary(pair => pair.Id, pair => pair.position, StringComparer.Ordinal);
        state.Items = state.Items
            .OrderBy(item => rank.TryGetValue(item.AppId, out var position) ? position : int.MaxValue)
            .ToList();
        var total = state.Items.Count;
        var index = 0;
        foreach (var item in state.Items)
        {
            index++;
            if (item.Status is InstallStatus.Success or InstallStatus.Skipped or InstallStatus.RebootRequired)
            {
                sessionLog.Report(item, InstallStatus.Success, item.Detail ?? CoreText.Get("InstallEngine_AlreadyComplete"), 1);
                continue;
            }

            if (item.Status is InstallStatus.Failed or InstallStatus.NeedsManualAction)
            {
                sessionLog.Line(CoreText.Format("InstallEngine_SkippingFailedAppUseRetry", item.Name));
                continue;
            }

            var app = profile.Apps.FirstOrDefault(a => a.Id == item.AppId);
            if (app is null || !app.Enabled)
            {
                item.Status = InstallStatus.Skipped;
                item.Detail = CoreText.Get("InstallEngine_RemovedFromTheProfile");
                _stateStore.Save(state);
                continue;
            }

            // A dependency that did not install stops the apps that need it (transitively, because
            // a stopped app is itself marked failed before its dependents are reached).
            var blocker = BlockingDependency(state, plan, item.AppId);
            var notAttempted = blocker is not null
                ? CoreText.Format("InstallEngine_NotAttempted", CoreText.Format("InstallEngine_NeedsWhichDidNotInstall", blocker.Name))
                : loopReasons.TryGetValue(item.AppId, out var loopReason) ? CoreText.Format("InstallEngine_NotAttempted", loopReason) : null;
            if (notAttempted is not null)
            {
                item.Status = InstallStatus.Failed;
                item.Error = notAttempted;
                item.Detail = notAttempted;
                item.NotAttempted = true;
                item.UpdatedUtc = DateTimeOffset.UtcNow;
                _stateStore.Save(state);
                sessionLog.Line($"[{item.Name}] {notAttempted}");
                sessionLog.Report(item, item.Status, item.Detail, null);
                continue;
            }

            var installer = _installers.FirstOrDefault(i => i.CanHandle(app.Source));
            if (installer is null)
            {
                item.Status = InstallStatus.NeedsManualAction;
                item.Error = CoreText.Format("InstallEngine_NoInstallerIsAvailableFor", app.Source);
                item.UpdatedUtc = DateTimeOffset.UtcNow;
                _stateStore.Save(state);
                sessionLog.Line($"[{item.Name}] {item.Error}");
                continue;
            }

            item.Status = InstallStatus.Preparing;
            item.Attempts++;
            item.Error = null;
            item.NotAttempted = false;
            item.Detail = CoreText.Format("InstallEngine_Starting", index, total);
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
                    item.Detail = result.Detail ?? CoreText.Get("InstallEngine_RestartRequired");
                    state.RebootRequested = true;
                    sessionLog.Line(CoreText.Format("InstallEngine_InstalledARestartIsRequired", item.Name));
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
                    sessionLog.Line(CoreText.Format("InstallEngine_NeedsManualAction", item.Name, item.Error));
                }
                else
                {
                    item.Status = InstallStatus.Failed;
                    item.Error = result.Error;
                    item.Detail = result.Detail;
                    sessionLog.Line(CoreText.Format("InstallEngine_Failed", item.Name, item.Error));
                }
            }
            catch (OperationCanceledException)
            {
                item.Status = InstallStatus.Queued;
                item.Detail = CoreText.Get("InstallEngine_Paused");
                item.UpdatedUtc = DateTimeOffset.UtcNow;
                _stateStore.Save(state);
                sessionLog.Line(CoreText.Format("InstallEngine_InstallationPaused", item.Name));
                throw;
            }
            catch (Exception ex)
            {
                item.Status = InstallStatus.Failed;
                item.Error = ex.Message;
                item.UpdatedUtc = DateTimeOffset.UtcNow;
                _log.Write(LogLevel.Error, $"Unexpected failure installing {app.Name}.", ex);
                sessionLog.Line(CoreText.Format("InstallEngine_UnexpectedFailure", item.Name, ex.Message));
            }

            _stateStore.Save(state);
// Post-install commands: one code path, decided and run by PostInstallStep at this moment.
            if (item.Status is InstallStatus.Success or InstallStatus.RebootRequired)
            {
                var outcome = await RunPostInstallAsync(profile, app, cancellationToken).ConfigureAwait(false);

                switch (outcome.Kind)
                {
                    case PostInstallOutcomeKind.Blocked:
                        item.Detail = CoreText.Format("InstallEngine_InstalledCommandNotRun", outcome.Reason);
                        sessionLog.Line(CoreText.Format("InstallEngine_PostInstallCommandNotRun", item.Name, outcome.Reason));
                        break;

                    case PostInstallOutcomeKind.Ran:
                        item.Detail = CoreText.Get("InstallEngine_InstalledPostInstallCommandOk");
                        sessionLog.Line(CoreText.Format("InstallEngine_PostInstallCommandOk", item.Name));
                        break;

                    case PostInstallOutcomeKind.Failed:
                    case PostInstallOutcomeKind.TimedOut:
                        item.Status = InstallStatus.PostInstallFailed;
                        item.Error = outcome.Kind == PostInstallOutcomeKind.TimedOut
                            ? CoreText.Get("InstallEngine_PostInstallCommandTimedOut")
                            : CoreText.Format("InstallEngine_PostInstallCommandFailed", outcome.ExitCode);
                        item.Detail = CoreText.Get("InstallEngine_InstalledPostInstallFailed");
                        sessionLog.Line(CoreText.Format("InstallEngine_InstalledPostInstallFailed2", item.Name, outcome.Kind));
                        break;
                }
            }

            

                        sessionLog.Report(item, item.Status, item.Detail, item.Status == InstallStatus.Success ? 1 : null);
        }

        state.IsFinished = true;
        state.LastError = state.FailedCount > 0
            ? CoreText.Plural("InstallEngine_AppsNeedAttention", state.FailedCount)
            : null;
        _stateStore.Save(state);

        sessionLog.Line(string.Empty);
        sessionLog.Line(CoreText.Format("InstallEngine_FinishedCompleteNeedAttention", state.CompletedCount, state.FailedCount));
        if (state.RebootRequested)
        {
            sessionLog.Line(CoreText.Get("InstallEngine_OneOrMoreInstallersRequested"));
        }

        return state;
    }

    /// <summary>The first dependency of <paramref name="appId"/> that ended without installing, if any.</summary>
    private static InstallItemState? BlockingDependency(InstallState state, InstallOrderResult plan, string appId)
    {
        if (!plan.Dependencies.TryGetValue(appId, out var dependencies))
        {
            return null;
        }

        foreach (var dependency in dependencies)
        {
            var dependencyItem = state.Items.FirstOrDefault(candidate => candidate.AppId == dependency);
            // Skipped counts as complete everywhere else in the app, so it does not block.
            if (dependencyItem?.Status is InstallStatus.Failed or InstallStatus.NeedsManualAction
                or InstallStatus.Cancelled)
            {
                return dependencyItem;
            }
        }

        return null;
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
                    item.Detail = CoreText.Get("InstallEngine_NotInTheActiveProfile");
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
                        $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}  {PathPrivacy.Current.ForLog(message)}{Environment.NewLine}",
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
