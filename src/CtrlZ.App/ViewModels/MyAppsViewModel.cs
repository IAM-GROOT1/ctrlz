using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CtrlZ.App.Services;
using CtrlZ.App.Views;
using CtrlZ.Core.Models;

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
        : base("My Apps", "Your portable reinstall profile. Tune the list before you move to a fresh Windows install.")
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
    private string _statusMessage = "Changes save automatically.";

    [ObservableProperty]
    private int _includedCount;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private string _sourceBreakdown = string.Empty;

    public bool HasApps => Apps.Count > 0;
    public bool IsEmpty => Apps.Count == 0;
    public string ProfileTitle => _services.State.ActiveProfile.Name;
    public string AppCountText => $"{Apps.Count} app{(Apps.Count == 1 ? string.Empty : "s")}";
    public string SelectionText => $"{IncludedCount} enabled of {Apps.Count}";
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
        var name = _dialogs.Prompt("New profile", "Create a separate app list for another PC or purpose.",
            "Profile name", "New profile");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        _services.State.CreateProfile(name);
        RefreshProfileList();
        ReloadApps();
        ShowStatus($"Created profile \"{name}\".");
    }

    [RelayCommand]
    private void RenameProfile()
    {
        var name = _dialogs.Prompt("Rename profile", "Profiles are portable JSON files.", "Profile name",
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
            Title = "Import a CTRL+Z profile",
            Filter = "CTRL+Z profile (*.json)|*.json|JSON files (*.json)|*.json",
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
            ShowStatus($"Imported \"{profile.Name}\".");
        }
        catch (Exception ex)
        {
            _dialogs.Error("That profile could not be imported.\n\n" + ex.Message);
        }
    }

    [RelayCommand]
    private void ExportProfile()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export profile",
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
            _services.State.ExportActive(dialog.FileName);
            ShowStatus($"Exported to {dialog.FileName}");
            _dialogs.Info($"Profile exported.\n\n{dialog.FileName}", "Profile exported");
        }
        catch (Exception ex)
        {
            _dialogs.Error("Could not export the profile.\n\n" + ex.Message);
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

        if (!_dialogs.Confirm("Delete profile",
                $"Delete \"{current.Name}\"? Its JSON file will be removed from CTRL+Z. Exported copies are untouched.",
                "Delete", "Cancel", danger: true))
        {
            return;
        }

        _services.State.DeleteProfile(current);
        RefreshProfileList();
        ReloadApps();
        ShowStatus("Profile deleted.");
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
    private void RemoveApp(ProfileAppRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (!_dialogs.Confirm("Remove app", $"Remove \"{row.Name}\" from this profile?", "Remove", "Cancel", danger: true))
        {
            return;
        }

        _services.State.RemoveApp(row.App);
        ReloadApps();
        ShowStatus($"Removed {row.Name}.");
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

        var window = new AppEditorWindow(row.App)
        {
            Owner = _main.GetWindow()
        };
        if (window.ShowDialog() == true && window.Result is not null)
        {
            _services.State.ReplaceApp(row.App, window.Result);
            ReloadApps();
            ShowStatus($"Updated {window.Result.Name}.");
        }
    }

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
        StatusMessage = "All apps enabled.";
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
        StatusMessage = $"Saved · {row.Name} {(row.IsIncluded ? "enabled" : "disabled")}";
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
