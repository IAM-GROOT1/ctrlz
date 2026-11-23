// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.App.Localization;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CtrlZ.App.Services;
using CtrlZ.Core.AppUpdates;
using CtrlZ.Core.Install;
using CtrlZ.Core.Logging;

namespace CtrlZ.App.ViewModels;

/// <summary>One app winget can update.</summary>
public sealed partial class AppUpdateRow : ObservableObject
{
    public AppUpdateRow(AppUpdate update)
    {
        Update = update;
        _isSelected = !update.RequiresExplicit;
    }

    public AppUpdate Update { get; }
    public string Name => Update.Name;
    public string Id => Update.Id;
    public string VersionsText => $"{Update.InstalledVersion ?? Loc.Get("AppUpdates_UnknownVersion")} → {Update.AvailableVersion}";
    public bool IsPinned => Update.RequiresExplicit;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _status = string.Empty;
}

/// <summary>An app from the profile installed from a GitHub release that has a newer release.</summary>
public sealed partial class GitHubUpdateRow : ObservableObject
{
    public GitHubUpdateRow(GitHubAppUpdate update)
    {
        Update = update;
    }

    public GitHubAppUpdate Update { get; }
    public string Name => Update.App.Name;
    public string Repository => Update.App.GitHubRepo ?? string.Empty;
    public string VersionsText => $"{Update.InstalledTag} → {Update.LatestTag}";

    [ObservableProperty]
    private string _status = string.Empty;
}

/// <summary>
/// Apps on this PC that winget can update. Nothing updates until the user chooses; pinned apps are
/// never part of "Update all"; a version can be ignored until a newer one appears. Shares the
/// install lock with the install queue and the command line.
/// </summary>
public sealed partial class AppUpdatesViewModel : PageViewModelBase
{
    private readonly AppServices _services;
    private readonly DialogService _dialogs;
    private readonly AppUpdateIgnoreStore _ignores;
    private IReadOnlyList<AppUpdate> _all = [];
    private CancellationTokenSource? _cts;
    private bool _loaded;

    public AppUpdatesViewModel(AppServices services, DialogService dialogs)
        : base(Loc.Get("AppUpdates_Updates"), Loc.Get("AppUpdates_AppsWingetCanUpdateOn"))
    {
        _services = services;
        _dialogs = dialogs;
        _ignores = new AppUpdateIgnoreStore(Path.Combine(services.Paths.State, AppUpdateIgnoreStore.FileName));
    }

    public ObservableCollection<AppUpdateRow> Rows { get; } = new();

    public bool HasRows => Rows.Count > 0;

    /// <summary>Apps of the active profile installed from a GitHub release with a newer release.</summary>
    public ObservableCollection<GitHubUpdateRow> GitHubRows { get; } = new();

    public bool HasGitHubRows => GitHubRows.Count > 0;

    [ObservableProperty]
    private string _summaryTitle = Loc.Get("AppUpdates_CheckForAppUpdates");

    [ObservableProperty]
    private string _statusLine = Loc.Get("AppUpdates_WingetIsAskedWhichInstalled");

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(UpdateSelectedCommand), nameof(UpdateAllCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _showIgnored;

    partial void OnShowIgnoredChanged(bool value) => Rebuild();

    public override async Task ActivateAsync()
    {
        if (!_loaded)
        {
            _loaded = true;
            await CheckAsync();
        }
    }

    private bool CanAct() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task CheckAsync()
    {
        IsBusy = true;
        StatusLine = Loc.Get("AppUpdates_AskingWingetForUpdates");
        try
        {
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _all = await _services.Winget.ListUpgradesAsync(_cts.Token);
            Rebuild();
            await CheckGitHubAsync(_cts.Token);
            StatusLine = Loc.Format("AppUpdates_CheckedPinnedAppsAreOnly", DateTime.Now);
        }
        catch (OperationCanceledException)
        {
            StatusLine = Loc.Get("AppUpdates_CheckCancelled");
        }
        catch (Exception ex)
        {
            _services.Log.Write(LogLevel.Error, "Checking for app updates failed.", ex);
            StatusLine = Loc.Get("AppUpdates_WingetCouldNotBeAsked");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task UpdateSelectedAsync() =>
        RunAsync(Rows.Where(row => row.IsSelected).Select(row => row.Update).ToList(), Loc.Get("AppUpdates_UpdateSelectedApps"));

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task UpdateAllAsync() =>
        RunAsync(AppUpdateRunner.ForUpdateAll(_all, _ignores), Loc.Get("AppUpdates_UpdateAllApps"));

    [RelayCommand]
    private void Ignore(AppUpdateRow? row)
    {
        if (row is null || IsBusy)
        {
            return;
        }

        _ignores.Ignore(row.Update);
        Rebuild();
        StatusLine = Loc.Format("AppUpdates_IsHiddenUntilANewer", row.Name, row.Update.AvailableVersion);
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private async Task CheckGitHubAsync(CancellationToken cancellationToken)
    {
        try
        {
            var updates = await GitHubUpdateChecker.CheckAsync(
                _services.State.ActiveProfile.Apps,
                _services.GitHubReleases,
                async (repository, token) => (await _services.GitHub.GetLatestAsync(repository, token)).TagName,
                cancellationToken);

            GitHubRows.Clear();
            foreach (var update in updates)
            {
                GitHubRows.Add(new GitHubUpdateRow(update));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _services.Log.Write(LogLevel.Warning, "Checking GitHub releases for updates failed.", ex);
        }
        finally
        {
            OnPropertyChanged(nameof(HasGitHubRows));
        }
    }

    /// <summary>Installs the newer release over the app, the way it was first installed. No post-install command runs.</summary>
    [RelayCommand]
    private async Task UpdateGitHubAsync(GitHubUpdateRow? row)
    {
        if (row is null || IsBusy)
        {
            return;
        }

        if (!_dialogs.Confirm(Loc.Get("AppUpdates_UpdateFromGitHub"),
                Loc.Format("AppUpdates_DownloadFromAndInstallIt", row.Name, row.Update.LatestTag, row.Repository, row.Update.InstalledTag) +
                Loc.Get("AppUpdates_CloseTheAppFirstThe"),
                Loc.Get("AppUpdates_Update2"), Loc.Get("AppUpdates_Cancel2")))
        {
            return;
        }

        using var runLock = InstallLock.TryAcquire();
        if (runLock is null)
        {
            StatusLine = Loc.Get("AppUpdates_AnotherInstallOrUpdateIs");
            return;
        }

        IsBusy = true;
        using var activity = _services.Activity.Begin(Loc.Get("AppUpdates_UpdatingApps"));
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        row.Status = Loc.Get("AppUpdates_Updating");
        try
        {
            var context = new InstallContext(
                _services.Paths, NullInstallLogSink.Instance, _services.Log, CtrlZ.Core.Services.ElevationService.IsElevated);
            var result = await _services.GitHubInstaller.InstallAsync(row.Update.App, context, _cts.Token);
            row.Status = result.Success
                ? Loc.Format("AppUpdates_UpdatedTo", row.Update.LatestTag)
                : result.Error ?? result.Detail ?? Loc.Get("AppUpdates_TheUpdateDidNotFinish");
            if (result.Success)
            {
                GitHubRows.Remove(row);
                OnPropertyChanged(nameof(HasGitHubRows));
                StatusLine = Loc.Format("AppUpdates_UpdatedTo2", row.Name, row.Update.LatestTag);
            }
        }
        catch (OperationCanceledException)
        {
            row.Status = Loc.Get("AppUpdates_Cancelled");
        }
        catch (Exception ex)
        {
            _services.Log.Write(LogLevel.Error, $"Updating {row.Name} from GitHub failed.", ex);
            row.Status = Loc.Get("AppUpdates_TheUpdateFailed") + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RunAsync(IReadOnlyList<AppUpdate> chosen, string title)
    {
        if (chosen.Count == 0)
        {
            StatusLine = Loc.Get("AppUpdates_NothingSelectedToUpdate");
            return;
        }

        var list = string.Join("\n", chosen.Take(15).Select(update =>
            $"• {update.Name}: {update.InstalledVersion ?? Loc.Get("AppUpdates_UnknownVersion")} → {update.AvailableVersion}"));
        if (chosen.Count > 15)
        {
            list += Loc.Format("AppUpdates_AndMore", chosen.Count - 15);
        }

        if (!_dialogs.Confirm(title,
                Loc.Plural("AppUpdates_WingetWillUpdate", chosen.Count, list) +
                Loc.Get("AppUpdates_CloseTheseAppsFirstWindows"),
                Loc.Get("AppUpdates_Update2"), Loc.Get("AppUpdates_Cancel2")))
        {
            return;
        }

        using var runLock = InstallLock.TryAcquire();
        if (runLock is null)
        {
            StatusLine = Loc.Get("AppUpdates_AnotherInstallOrUpdateIs");
            return;
        }

        IsBusy = true;
        using var activity = _services.Activity.Begin(Loc.Get("AppUpdates_UpdatingApps"));
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var rows = Rows.ToDictionary(row => row.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var update in chosen)
        {
            if (rows.TryGetValue(update.Id, out var row))
            {
                row.Status = Loc.Get("AppUpdates_Queued");
            }
        }

        try
        {
            var runner = new AppUpdateRunner(async (id, onLine, token) =>
            {
                if (rows.TryGetValue(id, out var current))
                {
                    current.Status = Loc.Get("AppUpdates_Updating");
                }

                var result = await _services.Winget.UpgradeAsync(id, line => onLine(line.Text), token);
                return (result.ExitCode, result.CombinedOutput);
            });

            var progress = new Progress<AppUpdateOutcome>(outcome =>
            {
                if (rows.TryGetValue(outcome.Update.Id, out var current))
                {
                    current.Status = outcome.Message;
                }
            });

            var outcomes = await runner.RunAsync(chosen, progress, null, _cts.Token);
            var updated = outcomes.Count(o => o.Kind is AppUpdateOutcomeKind.Updated or AppUpdateOutcomeKind.UpdatedRestartNeeded);
            var failed = outcomes.Count(o => o.Kind == AppUpdateOutcomeKind.Failed);
            var restart = outcomes.Any(o => o.Kind == AppUpdateOutcomeKind.UpdatedRestartNeeded);
            StatusLine = Loc.Format("AppUpdates_UpdatedFailed", updated, failed) + (restart ? Loc.Get("AppUpdates_RestartWindowsToFinish") : string.Empty);
            _services.Log.Write(LogLevel.Info, $"App updates finished: {updated} updated, {failed} failed.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Rebuild()
    {
        Rows.Clear();
        foreach (var update in _all.Where(update => ShowIgnored || !_ignores.IsIgnored(update)))
        {
            Rows.Add(new AppUpdateRow(update));
        }

        var pinned = Rows.Count(row => row.IsPinned);
        SummaryTitle = Rows.Count == 0
            ? Loc.Get("AppUpdates_YourAppsAreUpTo")
            : Loc.Plural("AppUpdates_UpdatesAvailable", Rows.Count);
        if (pinned > 0)
        {
            SummaryTitle += Loc.Format("AppUpdates_PinnedCount", pinned);
        }

        OnPropertyChanged(nameof(HasRows));
    }
}
