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
using CtrlZ.Core.Logging;
using CtrlZ.Core.Models;
using CtrlZ.Core.Services;

namespace CtrlZ.App.ViewModels;

public sealed partial class ScanViewModel : PageViewModelBase
{
    private readonly AppServices _services;
    private readonly DialogService _dialogs;
    private readonly MainViewModel _main;
    private CancellationTokenSource? _scanCts;
    private bool _activated;

    public ScanViewModel(AppServices services, DialogService dialogs, MainViewModel main)
        : base(Loc.Get("Scan_ScanThisPC2"), Loc.Get("Scan_FindInstalledProgramsAndMatch"))
    {
        _services = services;
        _dialogs = dialogs;
        _main = main;
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = FilterItem;
        ItemsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ScanItemViewModel.GroupName)));
        ItemsView.SortDescriptions.Add(new SortDescription(nameof(ScanItemViewModel.SortGroup), ListSortDirection.Ascending));
        ItemsView.SortDescriptions.Add(new SortDescription(nameof(ScanItemViewModel.Name), ListSortDirection.Ascending));
        Categories = new ObservableCollection<string>([Loc.Get("Scan_AllCategories")]);
        SelectedCategory = Loc.Get("Scan_AllCategories");
        MatchFilters = new ObservableCollection<string>([Loc.Get("Scan_AllApps"), Loc.Get("Scan_Matched2"), Loc.Get("Scan_Unmatched2")]);
        SelectedMatchFilter = Loc.Get("Scan_AllApps");
    }

    public ObservableCollection<ScanItemViewModel> Items { get; } = new();
    public ICollectionView ItemsView { get; }
    public ObservableCollection<string> Categories { get; }
    public ObservableCollection<string> MatchFilters { get; }

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedCategory;

    [ObservableProperty]
    private bool _groupBySource;

    [ObservableProperty]
    private string _selectedMatchFilter;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _hasScanned;

    [ObservableProperty]
    private double _scanProgress;

    [ObservableProperty]
    private string _scanStatus = Loc.Get("Scan_ReadyScanToFindInstalled");

    [ObservableProperty]
    private string? _warningText;

    [ObservableProperty]
    private int _selectedCount;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private int _matchedCount;

    [ObservableProperty]
    private int _unmatchedCount;

    [ObservableProperty]
    private string _lastScanText = string.Empty;

    public bool IsGroupedByCategory => !GroupBySource;
    public bool CanAddSelection => SelectedCount > 0;
    public string SelectionText => SelectedCount == 0
        ? $"{TotalCount} apps"
        : Loc.Format("Scan_SelectedOf", SelectedCount, TotalCount);
    public bool HasApps => Items.Count > 0;
    public bool HasWarning => !string.IsNullOrWhiteSpace(WarningText);

    public override async Task ActivateAsync()
    {
        if (!_activated && !HasScanned && !IsScanning)
        {
            _activated = true;
            await ScanAsync();
        }
    }

    partial void OnSearchTextChanged(string value) => ItemsView.Refresh();

    partial void OnSelectedCategoryChanged(string value) => ItemsView.Refresh();

    partial void OnSelectedMatchFilterChanged(string value) => ItemsView.Refresh();

    partial void OnWarningTextChanged(string? value) => OnPropertyChanged(nameof(HasWarning));

    partial void OnGroupBySourceChanged(bool value)
    {
        OnPropertyChanged(nameof(IsGroupedByCategory));
        ApplyGrouping();
    }

    partial void OnSelectedCountChanged(int value)
    {
        OnPropertyChanged(nameof(CanAddSelection));
        OnPropertyChanged(nameof(SelectionText));
    }

    partial void OnTotalCountChanged(int value) => OnPropertyChanged(nameof(SelectionText));

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning)
        {
            return;
        }

        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        IsScanning = true;
        HasScanned = false;
        WarningText = null;
        ScanProgress = 0;
        ScanStatus = Loc.Get("Scan_LookingForWingetPackages");

        try
        {
            var progress = new Progress<ScanProgress>(update =>
            {
                ScanStatus = update.Detail is null ? update.Step : $"{update.Step} · {update.Detail}";
                if (update.Total > 0)
                {
                    ScanProgress = Math.Clamp((double)update.Index / update.Total, 0, 1);
                }
            });

            var result = await _services.Scanner.ScanAsync(progress, _scanCts.Token);
            Items.Clear();
            foreach (var app in result.Apps)
            {
                var item = new ScanItemViewModel(app);
                item.SelectionChanged += OnItemSelectionChanged;
                Items.Add(item);
            }

            Categories.Clear();
            Categories.Add(Loc.Get("Scan_AllCategories"));
            foreach (var category in Items
                         .Select(i => i.App.Category.Display())
                         .Distinct()
                         .OrderBy(c => c, StringComparer.CurrentCultureIgnoreCase))
            {
                Categories.Add(category);
            }

            SelectedCategory = Loc.Get("Scan_AllCategories");
            ApplyGrouping();
            UpdateCounts();
            HasScanned = true;
            ScanProgress = 1;
            LastScanText = Loc.Format("Scan_Scanned", result.ScannedUtc.ToLocalTime());
            ScanStatus = Loc.Format("Scan_FoundProgramsMatchedUnmatched", result.Apps.Count, result.MatchedCount, result.UnmatchedCount);
            WarningText = result.Warnings.Count > 0 ? string.Join(" ", result.Warnings) : null;
            _services.Log.Write(LogLevel.Info, $"Scan page loaded {result.Apps.Count} apps.");
            ItemsView.Refresh();
        }
        catch (OperationCanceledException)
        {
            ScanStatus = Loc.Get("Scan_ScanCancelled");
        }
        catch (Exception ex)
        {
            _services.Log.Write(LogLevel.Error, "Scan failed.", ex);
            ScanStatus = Loc.Get("Scan_TheScanCouldNotBe");
            _dialogs.Error(Loc.Get("Scan_CTRLZCouldNotFinish") + ex.Message);
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

    [RelayCommand]
    private void SelectMatched()
    {
        foreach (var item in Items.Where(item => item.IsMatched && FilterItem(item)))
        {
            item.IsSelected = true;
        }
    }

    [RelayCommand]
    private void SelectAllVisible()
    {
        foreach (var item in Items.Where(FilterItem))
        {
            item.IsSelected = true;
        }
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var item in Items)
        {
            item.IsSelected = false;
        }
    }

    [RelayCommand]
    private void SetGroupSource() => GroupBySource = true;

    [RelayCommand]
    private void SetGroupCategory() => GroupBySource = false;

    [RelayCommand]
    private async Task FixMatchAsync(ScanItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var window = new WingetMatchWindow(_services.Winget, item.Name);
        if (_main.GetWindow() is { } owner)
        {
            window.Owner = owner;
        }

        if (window.ShowDialog() == true && window.SelectedPackage is not null)
        {
            item.ApplyMatch(window.SelectedPackage);
            if (!string.IsNullOrWhiteSpace(item.App.MatchReason))
            {
                item.App.Notes = item.App.MatchReason;
            }

            ApplyGrouping();
            UpdateCounts();
            ItemsView.Refresh();
        }

        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task AddSelectedAsync()
    {
        var selected = Items.Where(i => i.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            _dialogs.Info(Loc.Get("Scan_TickTheAppsYouWant"));
            return;
        }

        var unmatched = selected.Count(i => !i.IsMatched);
        if (unmatched > 0)
        {
            _dialogs.Info(
                Loc.Plural("Scan_SelectedNoMatch", unmatched) +
                Loc.Get("Scan_UseFixOnThoseRows"), Loc.Get("Scan_UnmatchedApps"));
            return;
        }

        var (added, skipped) = _services.State.AddApps(selected.Select(i => i.App.ToProfileEntry()));
        await _main.NavigateToAsync("apps");
        _main.MyApps.ShowStatus(added == 0
            ? Loc.Format("Scan_NoNewAppsAddedAlready", skipped)
            : Loc.Plural("Scan_AddedApps", added, _services.State.ActiveProfile.Name) +
              (skipped > 0 ? Loc.Format("Scan_AlreadyPresent", skipped) : "."));
    }

    [RelayCommand]
    private async Task SaveAsProfileAsync()
    {
        var selected = Items.Where(i => i.IsSelected && i.IsMatched).ToArray();
        if (selected.Length == 0)
        {
            _dialogs.Info(Loc.Get("Scan_TickAtLeastOneMatched"));
            return;
        }

        var name = _dialogs.Prompt(Loc.Get("Scan_SaveProfile"), Loc.Get("Scan_CreateANewProfileFrom"), Loc.Get("Scan_ProfileName"),
            $"{(Environment.MachineName.Length > 0 ? Environment.MachineName : "My")} PC");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var profile = _services.State.CreateProfile(name);
        var (added, _) = _services.State.AddApps(selected.Select(i => i.App.ToProfileEntry()));
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.Get("Scan_ExportProfileTitle"),
            Filter = "CTRL+Z profile (*.json)|*.json",
            FileName = $"{name}.json",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "CTRL+Z Profiles")
        };
        Directory.CreateDirectory(dialog.InitialDirectory);
        if (dialog.ShowDialog() == true)
        {
            _services.State.ExportActive(dialog.FileName);
            _dialogs.Info(
                Loc.Format("Scan_SavedProfileWithApps", profile.Name, added, dialog.FileName) +
                Loc.Get("Scan_KeepThisJSONWithYour"), Loc.Get("Scan_ProfileSaved"));
        }
        else
        {
            _dialogs.Info(Loc.Format("Scan_SavedProfileWithAppsIn", profile.Name, added), Loc.Get("Scan_ProfileSaved"));
        }

        await _main.NavigateToAsync("apps");
    }

    private bool FilterItem(object item)
    {
        if (item is not ScanItemViewModel app)
        {
            return false;
        }

        if (SelectedMatchFilter == Loc.Get("Scan_Matched2") && !app.IsMatched)
        {
            return false;
        }

        if (SelectedMatchFilter == Loc.Get("Scan_Unmatched2") && app.IsMatched)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(SelectedCategory) && SelectedCategory != Loc.Get("Scan_AllCategories") &&
            !app.App.Category.Display().Equals(SelectedCategory, StringComparison.CurrentCultureIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        var query = SearchText.Trim();
        return app.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               (app.Publisher?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
               (app.App.MatchedWingetId?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
               app.App.Category.Display().Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private void ApplyGrouping()
    {
        foreach (var item in Items)
        {
            var group = !item.IsMatched
                ? Loc.Get("Scan_Unmatched2")
                : GroupBySource
                    ? item.SourceLabel
                    : item.App.Category.Display();
            item.GroupName = group;
            item.SortGroup = (item.IsMatched ? "1" : "0") + group;
        }

        ItemsView.Refresh();
    }

    private void UpdateCounts()
    {
        TotalCount = Items.Count;
        MatchedCount = Items.Count(i => i.IsMatched);
        UnmatchedCount = TotalCount - MatchedCount;
        SelectedCount = Items.Count(i => i.IsSelected);
        OnPropertyChanged(nameof(HasApps));
        OnPropertyChanged(nameof(HasWarning));
    }

    private void OnItemSelectionChanged(object? sender, EventArgs e)
    {
        SelectedCount = Items.Count(i => i.IsSelected);
        OnPropertyChanged(nameof(CanAddSelection));
        OnPropertyChanged(nameof(SelectionText));
    }
}
