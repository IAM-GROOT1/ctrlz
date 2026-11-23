// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.App.Localization;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CtrlZ.App.Services;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Safety;

namespace CtrlZ.App.ViewModels;

/// <summary>One drift row with the actions that make sense for its group.</summary>
public sealed class DriftRow
{
    public DriftRow(DriftEntry entry)
    {
        Entry = entry;
        Detail = entry.Kind switch
        {
            DriftKind.Missing => entry.ProfileEntry?.Version is { Length: > 0 } version
                ? Loc.Format("Drift_SavedAs", version, entry.MatchReason)
                : entry.MatchReason,
            DriftKind.Extra => entry.InstalledVersion is { Length: > 0 } version
                ? Loc.Format("Drift_Installed", version)
                : Loc.Get("Drift_InstalledOnThisPC"),
            _ => Loc.Format("Drift_InstalledAvailable", entry.InstalledVersion, entry.AvailableVersion)
        };
    }

    public DriftEntry Entry { get; }
    public string Name => Entry.Name;
    public string Detail { get; }
    public string? PackageId => Entry.PackageId;

    public string ConfidenceLabel => Entry.Kind == DriftKind.Extra
        ? Loc.Get("Drift_NotInProfile")
        : Entry.Confidence switch
        {
            DriftConfidence.Exact => Loc.Get("Drift_ExactMatch"),
            DriftConfidence.Strong => Loc.Get("Drift_LikelyMatch"),
            DriftConfidence.Weak => Loc.Get("Drift_Uncertain"),
            _ => Loc.Get("Drift_NoMatch")
        };

    public bool CanAddToProfile => Entry.Kind == DriftKind.Extra;
    public bool CanRemoveFromProfile => Entry.Kind == DriftKind.Missing && Entry.ProfileEntry is not null;
    public bool CanInstall => Entry.Kind == DriftKind.Missing;
}

/// <summary>Drives the profile drift window: load, act, and stay in sync with the profile.</summary>
public sealed partial class DriftViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly DialogService _dialogs;
    private readonly MainViewModel _main;
    private readonly DriftAnalyzer _analyzer = new();
    private readonly SafetyStateStore _safety;

    public DriftViewModel(AppServices services, DialogService dialogs, MainViewModel main)
    {
        _services = services;
        _dialogs = dialogs;
        _main = main;
        _safety = new SafetyStateStore(Path.Combine(services.Paths.State, "safety-state.json"), services.Log);
    }

    public ObservableCollection<DriftRow> Missing { get; } = new();

    public ObservableCollection<DriftRow> Extra { get; } = new();

    public ObservableCollection<DriftRow> Outdated { get; } = new();

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusLine = string.Empty;

    [ObservableProperty]
    private string _summaryText = Loc.Get("Drift_ComparingYourSavedProfileWith");

    public event EventHandler? RequestClose;

    public bool HasMissing => Missing.Count > 0;

    public bool HasExtra => Extra.Count > 0;

    public bool HasOutdated => Outdated.Count > 0;

    public bool IsEmpty => Missing.Count == 0 && Extra.Count == 0 && Outdated.Count == 0;

    public string ProfileName => _services.State.ActiveProfile.Name;

    public async Task LoadAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusLine = Loc.Get("Drift_ScanningInstalledApps");

        try
        {
            var scan = await _services.Scanner.ScanAsync(null, CancellationToken.None);

            IReadOnlyList<Core.Models.WingetPackage> winget = [];
            try
            {
                StatusLine = Loc.Get("Drift_ReadingWingetVersions");
                winget = await _services.Winget.ListInstalledAsync();
            }
            catch (Exception ex)
            {
                // Versions are a bonus: drift still works without them.
                _services.Log.Write(
                    LogLevel.Warning,
                    "Could not read winget versions for drift. " + LogPrivacy.Describe(ex));
            }

            var state = _safety.Load();
            var report = _analyzer.Analyze(new DriftRequest
            {
                Profile = _services.State.ActiveProfile,
                Installed = scan.Apps,
                Winget = winget,
                IgnoredKeys = state.IgnoredDriftKeys
            });

            Apply(report);
            StatusLine = Loc.Plural("Drift_CheckedApps", scan.Apps.Count);
        }
        catch (Exception ex)
        {
            _services.Log.Write(LogLevel.Error, "Drift check failed. " + LogPrivacy.Describe(ex));
            StatusLine = Loc.Get("Drift_TheDriftCheckCouldNot");
            _dialogs.Error(Loc.Get("Drift_TheDriftCheckCouldNot2") + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RescanAsync() => await LoadAsync();

    [RelayCommand]
    private async Task AddToProfileAsync(DriftRow? row)
    {
        if (row?.Entry.Installed is not { } installed)
        {
            return;
        }

        var (added, _) = _services.State.AddApps([DriftAnalyzer.ToProfileEntry(installed)]);
        StatusLine = added > 0
            ? Loc.Format("Drift_AddedToYourProfile", row.Name)
            : Loc.Format("Drift_IsAlreadyInYourProfile", row.Name);

        await LoadAsync();
    }

    [RelayCommand]
    private async Task RemoveFromProfileAsync(DriftRow? row)
    {
        if (row?.Entry.ProfileEntry is not { } profileEntry)
        {
            return;
        }

        if (!_dialogs.Confirm(
                Loc.Get("Drift_RemoveFromProfile"),
                Loc.Format("Drift_RemoveFromTheAppStays", row.Name, ProfileName),
                Loc.Get("Drift_Remove2"), Loc.Get("Drift_Cancel")))
        {
            return;
        }

        _services.State.RemoveApp(profileEntry);
        StatusLine = Loc.Format("Drift_RemovedFromYourProfile", row.Name);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task IgnoreAsync(DriftRow? row)
    {
        if (row is null)
        {
            return;
        }

        var state = _safety.Load();
        SafetyStateStore.Add(state.IgnoredDriftKeys, row.Entry.Key);
        _safety.Save(state);

        StatusLine = Loc.Format("Drift_IgnoringFromNowOn", row.Name);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task InstallMissingAsync(DriftRow? row)
    {
        if (row is null)
        {
            return;
        }

        // Missing apps are already in the profile, so the install queue will pick them up.
        await _main.NavigateToAsync("install");
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Close() => RequestClose?.Invoke(this, EventArgs.Empty);

    private void Apply(DriftReport report)
    {
        Missing.Clear();
        Extra.Clear();
        Outdated.Clear();

        foreach (var entry in report.Missing)
        {
            Missing.Add(new DriftRow(entry));
        }

        foreach (var entry in report.Extra)
        {
            Extra.Add(new DriftRow(entry));
        }

        foreach (var entry in report.Outdated)
        {
            Outdated.Add(new DriftRow(entry));
        }

        if (report.IsEmpty)
        {
            SummaryText = Loc.Get("Drift_YourProfileMatchesThisPC")
                          + (report.IgnoredCount > 0 ? $" ({report.IgnoredCount} ignored)." : ".");
        }
        else
        {
            SummaryText =
                Loc.Format("Drift_MissingNotInProfileOutdated", report.Missing.Count, report.Extra.Count, report.Outdated.Count) +
                (report.IgnoredCount > 0 ? $" · {report.IgnoredCount} ignored" : string.Empty);
        }

        OnPropertyChanged(nameof(HasMissing));
        OnPropertyChanged(nameof(HasExtra));
        OnPropertyChanged(nameof(HasOutdated));
        OnPropertyChanged(nameof(IsEmpty));

        // Counts only: app names stay in the window, never in the log.
        _services.Log.Write(LogLevel.Info, SafetyDiagnostics.DriftSummary(report));
    }
}
