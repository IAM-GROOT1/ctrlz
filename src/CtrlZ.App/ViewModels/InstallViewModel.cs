// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

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
        : base("Install", "Review the queue and reinstall everything silently, with live status and a resumable session.")
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
    private string _statusMessage = "Review the queue, then install.";

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
    public string StartLabel => _state?.HasUnfinishedWork == true ? "Resume install" : "Install everything";
    public string CountsText => $"{CompletedCount} done · {FailedCount} need attention · {TotalCount} total";
    public bool HasLogs => Logs.Count > 0;
    public string ResumeTitle => _state?.HasUnfinishedWork == true ? "Resume available" : "Restart required";

    public override async Task ActivateAsync()
    {
        _activated = true;
        ElevationLabel = IsElevated ? "Running as administrator" : "Running as standard user";
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
                ? $"{saved.FailedCount} failed · {saved.CompletedCount} complete. Pick up where the last session stopped."
                : saved.RebootRequested
                    ? "Every app is installed. Restart Windows to finish the last installers."
                    : "The last install session is complete.";
            StatusMessage = saved.HasUnfinishedWork ? "A previous install can be resumed." : "Last session complete.";
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
                _dialogs.Info("This profile has no enabled apps. Add or enable apps in My Apps first.");
                return;
            }

            if (_state is not null)
            {
                if (!_dialogs.Confirm("Start a new install",
                        "A completed install session is on disk. Start a new one from the current profile?",
                        "Start new install", "Cancel"))
                {
                    return;
                }
            }

            state = _services.InstallEngine.CreateState(profile, null);
            _state = state;
            ProfileName = profile.Name;
            BuildRowsFromState(state);
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
        StatusMessage = "Stopping after the current installer…";
    }

    [RelayCommand]
    private void StartOver()
    {
        if (IsInstalling)
        {
            return;
        }

        if (_state is not null && !_dialogs.Confirm("Start over",
                "Discard the saved install session and build a fresh queue from the active profile?",
                "Start over", "Cancel", danger: true))
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
        StatusMessage = "Fresh queue ready.";
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
        if (!_dialogs.Confirm("Restart Windows",
                "Restart now to finish pending installers? Save any open work first.",
                "Restart now", "Cancel", danger: true))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 10") { UseShellExecute = true });
            StatusMessage = "Windows will restart in 10 seconds.";
        }
        catch (Exception ex)
        {
            _dialogs.Error("Could not restart Windows: " + ex.Message);
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
            "Administrator access",
            "Windows installers usually need administrator rights. CTRL+Z can restart itself elevated and automatically resume this install.\n\n" +
            "You can also continue unelevated, but machine-wide installers may fail.",
            "Restart as administrator",
            "Continue without admin",
            danger: false);
        if (result.Outcome == DialogOutcome.Primary)
        {
            _services.InstallStates.Save(state);
            if (!ElevationService.TryRelaunchElevated(["--auto-install"], out var error))
            {
                _dialogs.Error(error ?? "Could not restart as administrator.");
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

        _starting = true;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsInstalling = true;
        _state = state;
        HasResume = false;
        HasReboot = false;
        Summary = null;
        StatusMessage = retryFailed ? "Retrying failed apps…" : "Installing…";
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
                ? $"{finished.FailedCount} app{(finished.FailedCount == 1 ? string.Empty : "s")} need attention. Retry when ready."
                : finished.RebootRequested
                    ? "All installs finished. Restart Windows when convenient."
                    : "All installs finished successfully.";
            OnPropertyChanged(nameof(StartLabel));
            OnPropertyChanged(nameof(CanRetry));
            OnPropertyChanged(nameof(ResumeTitle));
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Install paused. Resume whenever you are ready.";
            HasResume = true;
            ResumeMessage = "The queue is saved. Resume after closing any open installers.";
        }
        catch (Exception ex)
        {
            _services.Log.Write(LogLevel.Error, "Install session failed.", ex);
            StatusMessage = "The install session stopped unexpectedly.";
            _dialogs.Error("CTRL+Z hit an error while installing.\n\n" + ex.Message);
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
            var row = new InstallRowViewModel(app.Name, app.Source, app.IconPath)
            {
                Status = InstallStatus.Queued,
                Detail = "Queued"
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
            var row = new InstallRowViewModel(item.Name, item.Source, app?.IconPath)
            {
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
