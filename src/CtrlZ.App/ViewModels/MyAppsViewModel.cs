// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.App.Localization;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CtrlZ.App.Services;
using CtrlZ.App.Views;
using CtrlZ.Core.Install;
using CtrlZ.Core.Localization;
using CtrlZ.Core.Models;
using CtrlZ.Core.Services;

namespace CtrlZ.App.ViewModels;

public sealed partial class MyAppsViewModel : PageViewModelBase
{
    private readonly AppServices _services;
    private readonly DialogService _dialogs;
    private readonly MainViewModel _main;
    private bool _suppressProfileChange;
    private bool _reloading;
    private bool _bulkUpdating;

    public MyAppsViewModel(AppServices services, DialogService dialogs, MainViewModel main)
        : base(Loc.Get("MyApps_MyApps"), Loc.Get("MyApps_YourPortableReinstallProfileTune"))
    {
        _services = services;
        _dialogs = dialogs;
        _main = main;
        AppsView = CollectionViewSource.GetDefaultView(Apps);
        AppsView.Filter = FilterApp;
        AppsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ProfileAppRow.GroupName)));
        RefreshProfileList();
        ReloadApps();
    }

    public ObservableCollection<ProfileSummary> Profiles { get; } = new();
    public ObservableCollection<ProfileAppRow> Apps { get; } = new();
    public ICollectionView AppsView { get; }

    [ObservableProperty]
    private ProfileSummary? _selectedProfile;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _groupBySource = true;

    [ObservableProperty]
    private string _statusMessage = Loc.Get("MyApps_ChangesSaveAutomatically");

    [ObservableProperty]
    private int _includedCount;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private string _sourceBreakdown = string.Empty;

    public bool HasApps => Apps.Count > 0;
    public bool IsEmpty => Apps.Count == 0;
    public string ProfileTitle => _services.State.ActiveProfile.Name;
    public string AppCountText => Loc.Plural("MyApps_AppCount", Apps.Count);
    public string SelectionText => Loc.Format("MyApps_EnabledOf", IncludedCount, Apps.Count);
    public bool CanInstall => IncludedCount > 0;

    public override Task ActivateAsync()
    {
        RefreshProfileList();
        ReloadApps();
        return Task.CompletedTask;
    }

    public void ReloadIfActive()
    {
        if (_reloading)
        {
            return;
        }

        var active = _services.State.ActiveProfile;
        if (SelectedProfile?.Id != active.Id)
        {
            RefreshProfileList();
        }

        ReloadApps();
    }

    public void ShowStatus(string message)
    {
        StatusMessage = message;
        OnPropertyChanged(nameof(ProfileTitle));
        OnPropertyChanged(nameof(SelectionText));
    }

    partial void OnSearchTextChanged(string value) => AppsView.Refresh();

    partial void OnGroupBySourceChanged(bool value)
    {
        ApplyGrouping();
        AppsView.Refresh();
    }

    partial void OnSelectedProfileChanged(ProfileSummary? value)
    {
        if (_suppressProfileChange || value is null)
        {
            return;
        }

        if (value.Id != _services.State.ActiveProfile.Id)
        {
            _suppressProfileChange = true;
            try
            {
                _services.State.SetActive(value);
            }
            finally
            {
                _suppressProfileChange = false;
            }
        }

        ReloadApps();
    }

    [RelayCommand]
    private void NewProfile()
    {
        var name = _dialogs.Prompt(Loc.Get("MyApps_NewProfile2"), Loc.Get("MyApps_CreateASeparateAppList"),
            Loc.Get("MyApps_ProfileName"), Loc.Get("MyApps_NewProfile2"));
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        _services.State.CreateProfile(name);
        RefreshProfileList();
        ReloadApps();
        ShowStatus(Loc.Format("MyApps_CreatedProfile", name));
    }

    [RelayCommand]
    private void RenameProfile()
    {
        var name = _dialogs.Prompt(Loc.Get("MyApps_RenameProfile2"), Loc.Get("MyApps_ProfilesArePortableJSONFiles"), Loc.Get("MyApps_ProfileName"),
            _services.State.ActiveProfile.Name);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        _services.State.RenameActiveProfile(name);
        RefreshProfileList();
        ReloadApps();
    }

    [RelayCommand]
    private void ImportProfile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.Get("MyApps_ImportACTRLZProfile"),
            Filter = Loc.Get("MyApps_ProfileFilter"),
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var profile = _services.State.ImportProfile(dialog.FileName);
            RefreshProfileList();
            ReloadApps();
            ShowStatus(Loc.Format("MyApps_Imported", profile.Name));
        }
        catch (Exception ex)
        {
            _dialogs.Error(Loc.Get("MyApps_ThatProfileCouldNotBe") + ex.Message);
        }
    }

    [RelayCommand]
    private void ExportProfile()
    {
        // Say what carries over before writing anything, and let notes stay behind.
        var summary = ProfileExport.Summarize(_services.State.ActiveProfile);
        var choice = _dialogs.Show(
            Loc.Get("MyApps_ExportForAnotherPC"),
            summary.Describe(),
            primary: Loc.Get("MyApps_ChooseWhereToSave"),
            secondary: Loc.Get("MyApps_Cancel"),
            checkText: summary.HasNotes ? Loc.Get("MyApps_IncludeMyNotesInThe") : null);
        if (choice.Outcome != Views.DialogOutcome.Primary)
        {
            return;
        }

        var includeNotes = summary.HasNotes && choice.IsChecked;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.Get("MyApps_ExportProfileTitle"),
            Filter = "CTRL+Z profile (*.json)|*.json",
            FileName = SafeName(_services.State.ActiveProfile.Name) + ".json",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "CTRL+Z Profiles")
        };
        Directory.CreateDirectory(dialog.InitialDirectory);
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            _services.State.ExportActive(dialog.FileName, includeNotes);
            ShowStatus(Loc.Format("MyApps_ExportedTo", dialog.FileName));
            _dialogs.Info((summary.HasNotes && !includeNotes
                    ? Loc.Format("MyApps_ProfileExportedWithoutNotes", dialog.FileName)
                    : Loc.Format("MyApps_ProfileExportedTo", dialog.FileName)) +
                Loc.Get("MyApps_CopyItToAUSB"), Loc.Get("MyApps_ProfileExported"));
        }
        catch (Exception ex)
        {
            _dialogs.Error(Loc.Get("MyApps_CouldNotExportTheProfile") + ex.Message);
        }
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        var current = SelectedProfile;
        if (current is null)
        {
            return;
        }

        if (!_dialogs.Confirm(Loc.Get("MyApps_DeleteProfile2"),
                Loc.Format("MyApps_DeleteItsJSONFileWill", current.Name),
                Loc.Get("MyApps_Delete"), Loc.Get("MyApps_Cancel"), danger: true))
        {
            return;
        }

        _services.State.DeleteProfile(current);
        RefreshProfileList();
        ReloadApps();
        ShowStatus(Loc.Get("MyApps_ProfileDeleted"));
    }

    [RelayCommand]
    private async Task AddAppAsync() => await _main.NavigateToAsync("add");

    [RelayCommand]
    private async Task BrowseCatalogAsync() => await _main.NavigateToAsync("catalog");

    [RelayCommand]
    private async Task ScanAsync() => await _main.NavigateToAsync("scan");

    [RelayCommand]
    private async Task InstallAppsAsync() => await _main.NavigateToAsync("install");

    [RelayCommand]
    private void CheckDrift() => _dialogs.ShowDrift(_services, _main);

    /// <summary>
    /// Adds apps from a winget, Scoop or Chocolatey export to the open profile. Matching uses the
    /// catalog already on this PC (no network), and the user sees what will be added first.
    /// </summary>
    [RelayCommand]
    private void ImportPackageList()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.Get("MyApps_ImportAnAppListFrom"),
            Filter = Loc.Get("MyApps_AppListFilter"),
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        PackageListImport import;
        try
        {
            var catalog = _services.Catalog.TryLoadCache() ?? _services.Catalog.LoadBundled();
            import = PackageListImporter.FromFile(dialog.FileName, catalog.Items);
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            _dialogs.Error(Loc.Get("MyApps_ThatFileCouldNotBe") + ex.Message +
                Loc.Get("MyApps_ImportListHint"),
                Loc.Get("MyApps_ImportAppList"));
            return;
        }

        if (import.Apps.Count == 0)
        {
            _dialogs.Info(Loc.Format("MyApps_NoAppsInThatList", import.KindName) +
                UnmatchedText(import.Unmatched), Loc.Get("MyApps_ImportAppList"));
            return;
        }

        var names = string.Join("\n", import.Apps.Take(15).Select(app => "• " + app.Name)) +
                    (import.Apps.Count > 15 ? Loc.Format("MyApps_AndMore", import.Apps.Count - 15) : string.Empty);
        if (!_dialogs.Confirm(Loc.Get("MyApps_ImportAppList"),
                Loc.Plural("MyApps_AddFromList", import.Apps.Count, import.KindName, _services.State.ActiveProfile.Name) +
                names + UnmatchedText(import.Unmatched) +
                Loc.Get("MyApps_AppsAlreadyInTheProfile"),
                Loc.Get("MyApps_AddApps2"), Loc.Get("MyApps_Cancel")))
        {
            return;
        }

        var (added, skipped) = _services.State.AddApps(import.Apps);
        _services.Log.Write(CtrlZ.Core.Logging.LogLevel.Info,
            $"Imported a {import.KindName} list: {added} added, {skipped} already present, {import.Unmatched.Count} not matched.");
        ReloadApps();
        ShowStatus(Loc.Plural("MyApps_AddedFromList", added, import.KindName) +
                   (skipped > 0 ? Loc.Format("MyApps_AlreadyPresent", skipped) : string.Empty) +
                   (import.Unmatched.Count > 0 ? Loc.Format("MyApps_NotMatched", import.Unmatched.Count) : string.Empty) + ".");
    }

    private static string UnmatchedText(IReadOnlyList<string> unmatched) => unmatched.Count == 0
        ? string.Empty
        : Loc.Format("MyApps_NotMatchedAddTheseBy", unmatched.Count) +
          string.Join(", ", unmatched.Take(20)) + (unmatched.Count > 20 ? ", …" : string.Empty);

    [RelayCommand]
    private void RemoveApp(ProfileAppRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (row.IsAutoDependency)
        {
            // Deleting it would only let the planner add it again; turning it off is remembered.
            if (!_dialogs.Confirm(Loc.Get("MyApps_RemoveDependency"),
                    Loc.Format("MyApps_WasAddedBecauseAnotherApp", row.Name),
                    Loc.Get("MyApps_TurnOff"), Loc.Get("MyApps_Cancel"), danger: true))
            {
                return;
            }

            _services.State.DisableApp(row.App);
            ReloadApps();
            ShowStatus(Loc.Format("MyApps_IsTurnedOffAndWill", row.Name));
            return;
        }

        if (!_dialogs.Confirm(Loc.Get("MyApps_RemoveApp"), Loc.Format("MyApps_RemoveFromThisProfile", row.Name), Loc.Get("MyApps_Remove"), Loc.Get("MyApps_Cancel"), danger: true))
        {
            return;
        }

        _services.State.RemoveApp(row.App);
        ReloadApps();
        ShowStatus(Loc.Format("MyApps_Removed", row.Name));
    }

    [RelayCommand]
    private void MoveUp(ProfileAppRow? row)
    {
        if (row is null)
        {
            return;
        }

        _services.State.MoveApp(row.App, -1);
        ReloadApps();
    }

    [RelayCommand]
    private void MoveDown(ProfileAppRow? row)
    {
        if (row is null)
        {
            return;
        }

        _services.State.MoveApp(row.App, 1);
        ReloadApps();
    }

    [RelayCommand]
    private void EditApp(ProfileAppRow? row)
    {
        if (row is null)
        {
            return;
        }

        var canEditCommand = PostInstallCommandForm.CanEdit(_services.State.OriginOf(_services.State.ActiveProfile));
        var window = new AppEditorWindow(row.App, canEditCommand, ConfirmRiskyCommand)
        {
            Owner = _main.GetWindow()
        };
        if (window.ShowDialog() == true && window.Result is not null)
        {
            _services.State.ReplaceApp(row.App, window.Result);
            ReloadApps();
            ShowStatus(Loc.Format("MyApps_Updated", window.Result.Name));
        }
    }

    /// <summary>Second, explicit confirmation before a risky command is saved.</summary>
    private bool ConfirmRiskyCommand(PostInstallCommand command) => _dialogs.Confirm(
        Loc.Get("MyApps_SaveARiskyCommand"),
        Loc.Format("MyApps_RiskyCommandSave", string.Join(", ", PostInstallCommandRisk.Scan(command)), CoreText.LeftToRight(command.Describe())),
        Loc.Get("MyApps_SaveRiskyCommand"),
        Loc.Get("MyApps_GoBack"),
        danger: true);

    [RelayCommand]
    private void SelectAllSources()
    {
        _bulkUpdating = true;
        try
        {
            foreach (var row in Apps)
            {
                row.IsIncluded = true;
            }
        }
        finally
        {
            _bulkUpdating = false;
        }

        _services.State.SaveActive();
        UpdateCounts();
        StatusMessage = Loc.Get("MyApps_AllAppsEnabled");
    }

    [RelayCommand]
    private void ToggleGrouping(string? mode)
    {
        GroupBySource = string.Equals(mode, "source", StringComparison.OrdinalIgnoreCase);
        ApplyGrouping();
        AppsView.Refresh();
    }

    private void RefreshProfileList()
    {
        var activeId = _services.State.ActiveProfile.Id;
        var selectedId = SelectedProfile?.Id ?? activeId;
        _suppressProfileChange = true;
        try
        {
            Profiles.Clear();
            foreach (var summary in _services.State.Profiles)
            {
                Profiles.Add(summary);
            }

            SelectedProfile = Profiles.FirstOrDefault(p => p.Id == selectedId)
                              ?? Profiles.FirstOrDefault(p => p.Id == activeId)
                              ?? Profiles.FirstOrDefault();
        }
        finally
        {
            _suppressProfileChange = false;
        }

        OnPropertyChanged(nameof(ProfileTitle));
        OnPropertyChanged(nameof(AppCountText));
    }

    private void ReloadApps()
    {
        _reloading = true;
        try
        {
            Apps.Clear();
            var order = 0;
            foreach (var app in _services.State.ActiveProfile.Apps)
            {
                var row = new ProfileAppRow(app, order++, OnRowChanged);
                Apps.Add(row);
            }

            ApplyGrouping();
            UpdateCounts();
            AppsView.Refresh();
            OnPropertyChanged(nameof(HasApps));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(ProfileTitle));
            OnPropertyChanged(nameof(AppCountText));
            OnPropertyChanged(nameof(SelectionText));
            OnPropertyChanged(nameof(CanInstall));
        }
        finally
        {
            _reloading = false;
        }
    }

    private void ApplyGrouping()
    {
        foreach (var row in Apps)
        {
            row.GroupName = GroupBySource ? row.SourceLabel : row.CategoryLabel;
        }
    }

    private void UpdateCounts()
    {
        TotalCount = Apps.Count;
        IncludedCount = Apps.Count(a => a.IsIncluded);
        SourceBreakdown = string.Join(" · ", Apps.GroupBy(a => a.SourceLabel)
            .OrderBy(g => g.Key)
            .Select(g => $"{g.Count()} {g.Key}"));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(HasApps));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void OnRowChanged(ProfileAppRow row)
    {
        if (_reloading || _bulkUpdating)
        {
            return;
        }

        _services.State.SaveActive();
        UpdateCounts();
        StatusMessage = row.IsIncluded ? Loc.Format("MyApps_SavedEnabled", row.Name) : Loc.Format("MyApps_SavedDisabled", row.Name);
    }

    private bool FilterApp(object item)
    {
        if (item is not ProfileAppRow row || string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        var query = SearchText.Trim();
        return row.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               (row.Publisher?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
               row.SourceLabel.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               (row.App.PackageId?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static string SafeName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));
}
