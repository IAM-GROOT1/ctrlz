// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.App.Localization;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CtrlZ.App.Services;
using CtrlZ.App.Views;
using CtrlZ.Core.Install;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Models;
using CtrlZ.Core.Services;

namespace CtrlZ.App.ViewModels;

public sealed partial class InstallViewModel : PageViewModelBase
{
    private readonly AppServices _services;
    private readonly DialogService _dialogs;
    private readonly MainViewModel _main;
    private CancellationTokenSource? _cts;
    private InstallState? _state;
    private bool _activated;
    private bool _autoStarted;
    private bool _starting;

    public InstallViewModel(AppServices services, DialogService dialogs, MainViewModel main)
        : base(Loc.Get("Install_Install"), Loc.Get("Install_ReviewTheQueueAndReinstall"))
    {
        _services = services;
        _dialogs = dialogs;
        _main = main;
        Logs = new ObservableCollection<string>();
    }

    public ObservableCollection<InstallRowViewModel> Items { get; } = new();
    public ObservableCollection<string> Logs { get; }

    [ObservableProperty]
    private string _profileName = string.Empty;

    [ObservableProperty]
    private bool _isInstalling;

    [ObservableProperty]
    private bool _hasResume;

    [ObservableProperty]
    private string? _resumeMessage;

    [ObservableProperty]
    private string _statusMessage = Loc.Get("Install_ReviewTheQueueThenInstall");

    [ObservableProperty]
    private double _overallProgress;

    [ObservableProperty]
    private int _completedCount;

    [ObservableProperty]
    private int _failedCount;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private bool _hasReboot;

    [ObservableProperty]
    private bool _showLogs = true;

    [ObservableProperty]
    private bool _autoScroll = true;

    [ObservableProperty]
    private string _elevationLabel = string.Empty;

    [ObservableProperty]
    private string? _logPath;

    [ObservableProperty]
    private string? _summary;

    public bool HasItems => Items.Count > 0;
    public bool IsElevated => ElevationService.IsElevated;
    public bool CanStart => !IsInstalling && Items.Count > 0;
    public bool CanRetry => !IsInstalling && _state is { FailedCount: > 0 };
    public bool CanCancel => IsInstalling;
    public bool IsEmpty => Items.Count == 0;
    public string StartLabel => _state?.HasUnfinishedWork == true ? Loc.Get("Install_ResumeInstall") : Loc.Get("Install_InstallEverything");
    public string CountsText => Loc.Format("Install_DoneNeedAttentionTotal", CompletedCount, FailedCount, TotalCount);
    public bool HasLogs => Logs.Count > 0;
    public string ResumeTitle => _state?.HasUnfinishedWork == true ? Loc.Get("Install_ResumeAvailable") : Loc.Get("Install_RestartRequired2");

    public override async Task ActivateAsync()
    {
        _activated = true;
        ElevationLabel = IsElevated ? Loc.Get("Install_RunningAsAdministrator") : Loc.Get("Install_RunningAsStandardUser");
        var saved = _services.InstallEngine.LoadSavedState();
        if (saved is not null)
        {
            _state = saved;
            ProfileName = saved.ProfileName;
            LogPath = saved.LogPath;
            BuildRowsFromState(saved);
            HasResume = saved.HasUnfinishedWork;
            HasReboot = saved.RebootRequested;
            ResumeMessage = saved.HasUnfinishedWork
                ? Loc.Format("Install_FailedCompletePickUpWhere", saved.FailedCount, saved.CompletedCount)
                : saved.RebootRequested
                    ? Loc.Get("Install_EveryAppIsInstalledRestart")
                    : Loc.Get("Install_TheLastInstallSessionIs");
            StatusMessage = saved.HasUnfinishedWork ? Loc.Get("Install_APreviousInstallCanBe") : Loc.Get("Install_LastSessionComplete");
            Summary = saved.LastError;
        }
        else
        {
            BuildPreview();
        }

        UpdateCounts();
        await Task.CompletedTask;
    }

    public async Task AutoStartAsync()
    {
        if (_autoStarted)
        {
            return;
        }

        _autoStarted = true;
        if (!_activated)
        {
            await ActivateAsync();
        }

        if (_state is { HasUnfinishedWork: true })
        {
            await RunStateAsync(_state, retryFailed: false, isAutoStart: true);
        }
    }

    partial void OnIsInstallingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanCancel));
    }

    partial void OnCompletedCountChanged(int value) => OnPropertyChanged(nameof(CountsText));

    partial void OnFailedCountChanged(int value) => OnPropertyChanged(nameof(CountsText));

    partial void OnTotalCountChanged(int value) => OnPropertyChanged(nameof(CountsText));

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsInstalling || _starting)
        {
            return;
        }

        var state = _state;
        var resuming = state is { HasUnfinishedWork: true };
        if (!resuming)
        {
            var profile = _services.State.ActiveProfile;
            if (profile.Apps.All(a => !a.Enabled))
            {
                _dialogs.Info(Loc.Get("Install_ThisProfileHasNoEnabled"));
                return;
            }

            if (_state is not null)
            {
                if (!_dialogs.Confirm(Loc.Get("Install_StartANewInstall"),
                        Loc.Get("Install_ACompletedInstallSessionIs"),
                        Loc.Get("Install_StartNewInstall"), Loc.Get("Install_Cancel")))
                {
                    return;
                }
            }

            // Known dependencies become visible, marked entries in the profile, so they can be
            // reviewed and turned off in My Apps; the plan then finds them instead of adding copies.
            _services.State.AddAutoDependencies(_services.InstallEngine.PlanOrder(profile).AutoAdded);

            var previous = _state;
            state = _services.InstallEngine.CreateState(profile, null);
            _state = state;
            ProfileName = profile.Name;
            BuildRowsFromState(state);

            // Final confirmation before anything runs; the user can turn this off in Settings.
            if (_services.State.Settings.ShowInstallPreview &&
                !_dialogs.ShowInstallPreview(_services, profile))
            {
                // Nothing ran, so nothing should be left to resume: put back the session that was
                // on disk before (or none), instead of the queue the preview was built from.
                DiscardUnstartedSession(previous);
                return;
            }
        }

        if (state is null)
        {
            return;
        }

        if (!await EnsureElevationAsync(state))
        {
            return;
        }

        await RunStateAsync(state, retryFailed: state.FailedCount > 0);
    }

    [RelayCommand]
    private async Task RetryFailedAsync()
    {
        if (_state is null || _state.FailedCount == 0 || IsInstalling)
        {
            return;
        }

        if (!await EnsureElevationAsync(_state))
        {
            return;
        }

        await RunStateAsync(_state, retryFailed: true);
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        StatusMessage = Loc.Get("Install_StoppingAfterTheCurrentInstaller");
    }

    private void DiscardUnstartedSession(InstallState? previous)
    {
        if (previous is not null)
        {
            _services.InstallStates.Save(previous);
            _state = previous;
            ProfileName = previous.ProfileName;
            BuildRowsFromState(previous);
        }
        else
        {
            _services.InstallStates.Clear();
            _state = null;
            LogPath = null;
            BuildPreview();
        }

        HasResume = previous is { HasUnfinishedWork: true };
        UpdateCounts();
        StatusMessage = Loc.Get("Install_InstallCancelledNothingRan");
    }

    [RelayCommand]
    private void StartOver()
    {
        if (IsInstalling)
        {
            return;
        }

        if (_state is not null && !_dialogs.Confirm(Loc.Get("Install_StartOver2"),
                Loc.Get("Install_DiscardTheSavedInstallSession"),
                Loc.Get("Install_StartOver2"), Loc.Get("Install_Cancel"), danger: true))
        {
            return;
        }

        _services.InstallStates.Clear();
        _state = null;
        HasResume = false;
        HasReboot = false;
        Summary = null;
        BuildPreview();
        UpdateCounts();
        StatusMessage = Loc.Get("Install_FreshQueueReady");
    }

    [RelayCommand]
    private void ClearLog()
    {
        Logs.Clear();
        OnPropertyChanged(nameof(HasLogs));
    }

    [RelayCommand]
    private void ToggleLogs()
    {
        ShowLogs = !ShowLogs;
    }

    [RelayCommand]
    private void OpenLog()
    {
        var path = _state?.LogPath ?? LogPath;
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_services.Paths.Logs}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            _services.Log.Write(LogLevel.Warning, "Could not open install log.", ex);
        }
    }

    [RelayCommand]
    private void RestartWindows()
    {
        if (!_dialogs.Confirm(Loc.Get("Install_RestartWindows2"),
                Loc.Get("Install_RestartNowToFinishPending"),
                Loc.Get("Install_RestartNow"), Loc.Get("Install_Cancel"), danger: true))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 10") { UseShellExecute = true });
            StatusMessage = Loc.Get("Install_WindowsWillRestartIn10");
        }
        catch (Exception ex)
        {
            _dialogs.Error(Loc.Get("Install_CouldNotRestartWindows") + ex.Message);
        }
    }

    public Task NavigateToAsync(string key) => _main.NavigateToAsync(key);

    private Task<bool> EnsureElevationAsync(InstallState state)
    {
        if (ElevationService.IsElevated)
        {
            return Task.FromResult(true);
        }

        var result = _dialogs.Show(
            Loc.Get("Install_AdministratorAccess"),
            Loc.Get("Install_WindowsInstallersUsuallyNeedAdministrato") +
            Loc.Get("Install_YouCanAlsoContinueUnelevated"),
            Loc.Get("Install_RestartAsAdministrator"),
            Loc.Get("Install_ContinueWithoutAdmin"),
            danger: false);
        if (result.Outcome == DialogOutcome.Primary)
        {
            _services.InstallStates.Save(state);
            if (!ElevationService.TryRelaunchElevated(["--auto-install"], out var error))
            {
                _dialogs.Error(error ?? Loc.Get("Install_CouldNotRestartAsAdministrator"));
                return Task.FromResult(false);
            }

            Application.Current.Shutdown();
            return Task.FromResult(false);
        }

        return Task.FromResult(result.Outcome == DialogOutcome.Secondary);
    }

    private async Task RunStateAsync(InstallState state, bool retryFailed, bool isAutoStart = false)
    {
        if (_starting || IsInstalling)
        {
            return;
        }

        // Every run passes here (new, resume, retry, elevated auto-start). Resolve the exact profile
        // the engine will run, then ask for the command confirmations it needs before anything starts.
        if (!InstallStartGuard.MayStart(
                state,
                _services.State.LoadForRun,
                profile => _dialogs.ConfirmPostInstallCommands(_services, profile)))
        {
            StatusMessage = Loc.Get("Install_InstallNotStartedThePost");
            return;
        }

        // Shared with the command line: only one install run on this PC at a time.
        using var runLock = InstallLock.TryAcquire();
        if (runLock is null)
        {
            StatusMessage = Loc.Get("Install_AnotherCTRLZInstallIs");
            return;
        }

        _starting = true;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsInstalling = true;
        // Keeps the updater from prompting or applying while the queue is running.
        using var activity = _services.Activity.Begin(Loc.Get("Install_InstallingApps"));
        _state = state;
        HasResume = false;
        HasReboot = false;
        Summary = null;
        StatusMessage = retryFailed ? Loc.Get("Install_RetryingFailedApps") : Loc.Get("Install_Installing");
        Logs.Clear();
        OnPropertyChanged(nameof(HasLogs));
        BuildRowsFromState(state);
        UpdateCounts();
        LogPath = state.LogPath;

        try
        {
            var sink = new UiInstallSink(this, Application.Current.Dispatcher);
            var finished = await _services.InstallEngine.RunAsync(state, sink, retryFailed, _cts.Token);
            _state = finished;
            BuildRowsFromState(finished);
            HasReboot = finished.RebootRequested && !finished.HasUnfinishedWork;
            Summary = finished.LastError;
            StatusMessage = finished.HasUnfinishedWork
                ? Loc.Plural("Install_AppsNeedAttention", finished.FailedCount)
                : finished.RebootRequested
                    ? Loc.Get("Install_AllInstallsFinishedRestartWindows")
                    : Loc.Get("Install_AllInstallsFinishedSuccessfully");
            OnPropertyChanged(nameof(StartLabel));
            OnPropertyChanged(nameof(CanRetry));
            OnPropertyChanged(nameof(ResumeTitle));
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Loc.Get("Install_InstallPausedResumeWheneverYou");
            HasResume = true;
            ResumeMessage = Loc.Get("Install_TheQueueIsSavedResume");
        }
        catch (Exception ex)
        {
            _services.Log.Write(LogLevel.Error, "Install session failed.", ex);
            StatusMessage = Loc.Get("Install_TheInstallSessionStoppedUnexpectedly");
            _dialogs.Error(Loc.Get("Install_CTRLZHitAnError") + ex.Message);
        }
        finally
        {
            IsInstalling = false;
            _starting = false;
            UpdateCounts();
        }
    }

    private void BuildPreview()
    {
        Items.Clear();
        var apps = _services.State.ActiveProfile.Apps.Where(a => a.Enabled).ToArray();
        foreach (var app in apps)
        {
            var row = new InstallRowViewModel(app.Name, app.Source, app.IconPath, app.PackageId)
            {
                Note = app.Notes,
                Status = InstallStatus.Queued,
                Detail = Loc.Get("Install_Queued")
            };
            Items.Add(row);
        }

        ProfileName = _services.State.ActiveProfile.Name;
        _state = null;
        HasResume = false;
        Summary = null;
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(StartLabel));
        OnPropertyChanged(nameof(CanStart));
    }

    private void BuildRowsFromState(InstallState state)
    {
        Items.Clear();
        var profile = state.ProfileSnapshot;
        foreach (var item in state.Items)
        {
            var app = profile?.Apps.FirstOrDefault(a => a.Id == item.AppId);
            var row = new InstallRowViewModel(item.Name, item.Source, app?.IconPath, app?.PackageId)
            {
                Note = app?.Notes,
                Status = item.Status,
                Detail = item.Detail ?? item.Error ?? item.Status.ToString(),
                Error = item.Error,
                Attempts = item.Attempts,
                Progress = item.Status is InstallStatus.Success or InstallStatus.RebootRequired or InstallStatus.Skipped ? 1 : 0
            };
            Items.Add(row);
        }

        ProfileName = state.ProfileName;
        LogPath = state.LogPath;
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(StartLabel));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanRetry));
    }

    private void UpdateCounts()
    {
        TotalCount = Items.Count;
        CompletedCount = Items.Count(i => i.Status is InstallStatus.Success or InstallStatus.Skipped or InstallStatus.RebootRequired);
        FailedCount = Items.Count(i => i.Status is InstallStatus.Failed or InstallStatus.NeedsManualAction);
        OverallProgress = TotalCount == 0
            ? 0
            : Items.Sum(i => i.IsDone || i.Status is InstallStatus.Failed or InstallStatus.NeedsManualAction ? 1 : Math.Clamp(i.Progress, 0, 1)) /
              TotalCount * 100;
        OnPropertyChanged(nameof(CountsText));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(StartLabel));
    }

    private void ApplyProgress(InstallProgress progress)
    {
        var row = Items.FirstOrDefault(i => i.Name == progress.AppName);
        if (row is null)
        {
            return;
        }

        row.Update(progress.Status, progress.Detail, progress.Fraction);
        UpdateCounts();
    }

    private void AppendLog(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            Logs.Add(string.Empty);
        }
        else
        {
            Logs.Add(line);
        }

        while (Logs.Count > 2500)
        {
            Logs.RemoveAt(0);
        }

        OnPropertyChanged(nameof(HasLogs));
    }

    private sealed class UiInstallSink : IInstallLogSink
    {
        private readonly InstallViewModel _viewModel;
        private readonly Dispatcher _dispatcher;

        public UiInstallSink(InstallViewModel viewModel, Dispatcher dispatcher)
        {
            _viewModel = viewModel;
            _dispatcher = dispatcher;
        }

        public void Line(string message)
        {
            _dispatcher.BeginInvoke(() => _viewModel.AppendLog(message));
        }

        public void Progress(InstallProgress progress)
        {
            _dispatcher.BeginInvoke(() => _viewModel.ApplyProgress(progress));
        }
    }
}
