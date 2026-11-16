// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CtrlZ.Core.Models;

namespace CtrlZ.App.ViewModels;

public sealed partial class BrowserItemViewModel : ObservableObject
{
    private readonly Action _changed;
    private bool _updatingSelection;

    public BrowserItemViewModel(DetectedBrowser browser, Action changed)
    {
        Browser = browser;
        _changed = changed;
        Profiles = new ObservableCollection<BrowserProfileChoiceViewModel>(
            browser.Profiles.Select(profile => new BrowserProfileChoiceViewModel(profile, OnProfileSelectionChanged)));
        _isExpanded = browser.Id.Equals("helium", StringComparison.OrdinalIgnoreCase);
    }

    public DetectedBrowser Browser { get; }
    public ObservableCollection<BrowserProfileChoiceViewModel> Profiles { get; }
    public string Name => Browser.DisplayName;
    public string Id => Browser.Id;
    public BrowserFamily Family => Browser.Family;
    public string FamilyLabel => Browser.FamilyLabel;
    public string? IconPath => Browser.ExecutablePath;
    public string VersionText => string.IsNullOrWhiteSpace(Browser.Version) ? "Version unknown" : $"v{Browser.Version}";
    public string StatusText => Browser.StatusText;
    public string UserDataPath => Browser.UserDataPath ?? string.Empty;
    public bool IsCustom => Browser.IsCustom;
    public bool NeedsUserDataPath => Browser.NeedsUserDataPath;
    public bool CanSelect => Browser.CanBackup && Profiles.Count > 0;
    public int ProfileCount => Profiles.Count;
    public string ProfileCountText => $"{Profiles.Count} profile{(Profiles.Count == 1 ? string.Empty : "s")}";
    public IReadOnlyList<BrowserProfile> SelectedProfiles => Profiles.Where(p => p.IsSelected).Select(p => p.Profile).ToList();

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private string _estimatedText = string.Empty;

    partial void OnIsSelectedChanged(bool value)
    {
        if (_updatingSelection)
        {
            return;
        }

        _updatingSelection = true;
        try
        {
            foreach (var profile in Profiles)
            {
                profile.IsSelected = value;
            }
        }
        finally
        {
            _updatingSelection = false;
        }

        _changed();
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void SelectAllProfiles()
    {
        SetAllProfiles(true);
    }

    [RelayCommand]
    private void ClearProfiles()
    {
        SetAllProfiles(false);
    }

    public void SetAllProfiles(bool selected)
    {
        _updatingSelection = true;
        try
        {
            foreach (var profile in Profiles)
            {
                profile.IsSelected = selected;
            }
        }
        finally
        {
            _updatingSelection = false;
        }

        IsSelected = Profiles.Count > 0 && Profiles.All(p => p.IsSelected);
        _changed();
    }

    private void OnProfileSelectionChanged()
    {
        if (_updatingSelection)
        {
            return;
        }

        _updatingSelection = true;
        try
        {
            IsSelected = Profiles.Count > 0 && Profiles.All(p => p.IsSelected);
        }
        finally
        {
            _updatingSelection = false;
        }

        _changed();
    }
}

public sealed partial class BrowserProfileChoiceViewModel : ObservableObject
{
    private readonly Action _changed;

    public BrowserProfileChoiceViewModel(BrowserProfile profile, Action changed)
    {
        Profile = profile;
        _changed = changed;
    }

    public BrowserProfile Profile { get; }
    public string Name => Profile.DisplayName;
    public string Detail => Profile.UserName is null ? Profile.DirectoryName : $"{Profile.DirectoryName} · {Profile.UserName}";
    public bool IsDefault => Profile.IsDefault;

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _changed();
}

public sealed partial class RestoreProfileChoiceViewModel : ObservableObject
{
    public RestoreProfileChoiceViewModel(BrowserBackupProfileManifest profile)
    {
        Profile = profile;
    }

    public BrowserBackupProfileManifest Profile { get; }
    public string Name => string.IsNullOrWhiteSpace(Profile.DisplayName) ? Profile.DirectoryName : Profile.DisplayName;
    public string Detail => Profile.DirectoryName;

    [ObservableProperty]
    private bool _isSelected = true;
}
