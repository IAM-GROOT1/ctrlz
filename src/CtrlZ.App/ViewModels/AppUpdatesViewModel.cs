// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

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
    public string VersionsText => $"{Update.InstalledVersion ?? "unknown"} → {Update.AvailableVersion}";
    public bool IsPinned => Update.RequiresExplicit;

    [ObservableProperty]
    private bool _isSelected;

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
        : base("Updates", "Apps winget can update on this PC. Nothing changes until you choose.")
    {
        _services = services;
        _dialogs = dialogs;
        _ignores = new AppUpdateIgnoreStore(Path.Combine(services.Paths.State, AppUpdateIgnoreStore.FileName));
    }

    public ObservableCollection<AppUpdateRow> Rows { get; } = new();

    public bool HasRows => Rows.Count > 0;

    [ObservableProperty]
    private string _summaryTitle = "Check for app updates";

    [ObservableProperty]
    private string _statusLine = "winget is asked which installed apps have a newer version.";

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
        StatusLine = "Asking winget for updates…";
        try
        {
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _all = await _services.Winget.ListUpgradesAsync(_cts.Token);
            Rebuild();
            StatusLine = $"Checked {DateTime.Now:HH:mm}. Pinned apps are only updated when you pick them.";
        }
        catch (OperationCanceledException)
        {
            StatusLine = "Check cancelled.";
        }
        catch (Exception ex)
        {
            _services.Log.Write(LogLevel.Error, "Checking for app updates failed.", ex);
            StatusLine = "winget could not be asked for updates. Check that App Installer is installed (Settings).";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task UpdateSelectedAsync() =>
        RunAsync(Rows.Where(row => row.IsSelected).Select(row => row.Update).ToList(), "Update selected apps");

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task UpdateAllAsync() =>
        RunAsync(AppUpdateRunner.ForUpdateAll(_all, _ignores), "Update all apps");

    [RelayCommand]
    private void Ignore(AppUpdateRow? row)
    {
        if (row is null || IsBusy)
        {
            return;
        }

        _ignores.Ignore(row.Update);
        Rebuild();
        StatusLine = $"{row.Name} {row.Update.AvailableVersion} is hidden until a newer version appears.";
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private async Task RunAsync(IReadOnlyList<AppUpdate> chosen, string title)
    {
        if (chosen.Count == 0)
        {
            StatusLine = "Nothing selected to update.";
            return;
        }

        var list = string.Join("\n", chosen.Take(15).Select(update =>
            $"• {update.Name}: {update.InstalledVersion ?? "unknown"} → {update.AvailableVersion}"));
        if (chosen.Count > 15)
        {
            list += $"\n…and {chosen.Count - 15} more";
        }

        if (!_dialogs.Confirm(title,
                $"winget will update {chosen.Count} app{(chosen.Count == 1 ? string.Empty : "s")}, one after another:\n\n{list}\n\n" +
                "Close these apps first. Windows may ask for permission for apps installed for all users.",
                "Update", "Cancel"))
        {
            return;
        }

        using var runLock = InstallLock.TryAcquire();
        if (runLock is null)
        {
            StatusLine = "Another install or update is running on this PC. Try again when it has finished.";
            return;
        }

        IsBusy = true;
        using var activity = _services.Activity.Begin("Updating apps");
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var rows = Rows.ToDictionary(row => row.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var update in chosen)
        {
            if (rows.TryGetValue(update.Id, out var row))
            {
                row.Status = "Queued";
            }
        }

        try
        {
            var runner = new AppUpdateRunner(async (id, onLine, token) =>
            {
                if (rows.TryGetValue(id, out var current))
                {
                    current.Status = "Updating…";
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
            StatusLine = $"{updated} updated, {failed} failed." + (restart ? " Restart Windows to finish." : string.Empty);
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
            ? "Your apps are up to date"
            : $"{Rows.Count} app update{(Rows.Count == 1 ? string.Empty : "s")} available";
        if (pinned > 0)
        {
            SummaryTitle += $" ({pinned} pinned)";
        }

        OnPropertyChanged(nameof(HasRows));
    }
}
