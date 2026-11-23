// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.App.Localization;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CtrlZ.App.Services;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Models;
using CtrlZ.Core.Services;

namespace CtrlZ.App.ViewModels;

public sealed partial class CatalogItemViewModel : ObservableObject
{
    private bool _isSelected;
    public CatalogItemViewModel(WinUtilCatalogItem item) => Item = item;
    public WinUtilCatalogItem Item { get; }
    public string Name => Item.Name;
    public string? WingetId => Item.WingetId;
    public string Category => Item.CategoryLabel;
    public string? Description => Item.Description;
    public string? Link => Item.Link;
    public bool IsFoss => Item.IsFoss;
    public bool CanInstall => Item.CanInstall;
    public string IdText => Item.WingetId ?? Loc.Get("Catalog_NoWingetPackage");
    public string FallbackText => Item.Name;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public event EventHandler? SelectionChanged;
}

public sealed partial class CatalogViewModel : PageViewModelBase
{
    private readonly AppServices _services;
    private readonly DialogService _dialogs;
    private readonly MainViewModel _main;
    private bool _loaded;

    public CatalogViewModel(AppServices services, DialogService dialogs, MainViewModel main)
        : base(Loc.Get("Catalog_Catalog"), Loc.Get("Catalog_RecommendedAppsFromChrisTitus"))
    {
        _services = services;
        _dialogs = dialogs;
        _main = main;
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = FilterItem;
        ItemsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CatalogItemViewModel.Category)));
        Categories = new ObservableCollection<string>([Loc.Get("Catalog_AllCategories")]);
        SelectedCategory = Loc.Get("Catalog_AllCategories");
    }

    public ObservableCollection<CatalogItemViewModel> Items { get; } = new();
    public ICollectionView ItemsView { get; }
    public ObservableCollection<string> Categories { get; }

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedCategory;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _catalogStatus = Loc.Get("Catalog_NotLoaded");

    [ObservableProperty]
    private string? _warningText;

    [ObservableProperty]
    private string _statusMessage = Loc.Get("Catalog_PickAppsFromTheCatalog");

    [ObservableProperty]
    private int _selectedCount;

    [ObservableProperty]
    private int _totalCount;

    public bool HasItems => Items.Count > 0;
    public bool HasWarning => !string.IsNullOrWhiteSpace(WarningText);
    public bool CanAdd => SelectedCount > 0;
    public string SelectionText => SelectedCount == 0 ? Loc.Get("Catalog_NothingSelected") : $"{SelectedCount} selected";
    public string CatalogSource => CatalogStatus;

    public override async Task ActivateAsync()
    {
        if (!_loaded)
        {
            _loaded = true;
            // First visit goes to the live source; the service falls back to the cache or
            // embedded snapshot if GitHub is unavailable.
            await LoadAsync(force: true);
        }
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync(force: true);

    partial void OnSearchTextChanged(string value) => ItemsView.Refresh();

    partial void OnSelectedCategoryChanged(string value) => ItemsView.Refresh();

    partial void OnWarningTextChanged(string? value) => OnPropertyChanged(nameof(HasWarning));

    partial void OnSelectedCountChanged(int value)
    {
        OnPropertyChanged(nameof(CanAdd));
        OnPropertyChanged(nameof(SelectionText));
    }

    [RelayCommand]
    private void SelectAllVisible()
    {
        foreach (var item in ItemsView.Cast<CatalogItemViewModel>().Where(i => i.CanInstall))
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
    private void AddSelected()
    {
        var selected = Items.Where(i => i.IsSelected && i.CanInstall).ToArray();
        if (selected.Length == 0)
        {
            _dialogs.Info(Loc.Get("Catalog_SelectAtLeastOneCatalog"));
            return;
        }

        var apps = selected.Select(i => new AppEntry
        {
            Name = i.Name,
            Source = AppSource.Winget,
            PackageId = i.WingetId,
            Category = i.Item.CategoryEnum,
            Notes = Loc.Get("Catalog_AddedFromTheWinUtilCatalog")
        });
        var (added, skipped) = _services.State.AddApps(apps);
        StatusMessage = added == 0
            ? Loc.Format("Catalog_NoNewAppsAddedAlready", skipped)
            : Loc.Plural("Catalog_AddedApps", added, _services.State.ActiveProfile.Name) +
              (skipped > 0 ? Loc.Format("Catalog_AlreadyPresent", skipped) : ".");
        foreach (var item in selected)
        {
            item.IsSelected = false;
        }

        _dialogs.Info(StatusMessage, Loc.Get("Catalog_Catalog"));
    }

    [RelayCommand]
    private void OpenLink(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _services.Log.Write(LogLevel.Warning, "Could not open catalog link.", ex);
        }
    }

    private async Task LoadAsync(bool force)
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        if (Items.Count == 0)
        {
            StatusMessage = force ? Loc.Get("Catalog_RefreshingFromGitHub") : Loc.Get("Catalog_LoadingTheAppCatalog");
        }

        try
        {
            var catalog = await _services.Catalog.LoadAsync(force);
            Items.Clear();
            foreach (var item in catalog.Items)
            {
                var row = new CatalogItemViewModel(item);
                row.SelectionChanged += OnItemSelectionChanged;
                Items.Add(row);
            }

            Categories.Clear();
            Categories.Add(Loc.Get("Catalog_AllCategories"));
            foreach (var category in Items.Select(i => i.Category).Distinct()
                         .OrderBy(c => c, StringComparer.CurrentCultureIgnoreCase))
            {
                Categories.Add(category);
            }

            SelectedCategory = Loc.Get("Catalog_AllCategories");
            CatalogStatus = catalog.StatusText;
            WarningText = catalog.Warning;
            TotalCount = Items.Count;
            StatusMessage = catalog.Warning is null
                ? Loc.Format("Catalog_ShowingRecommendedApps", Items.Count)
                : Loc.Get("Catalog_ShowingALocalCatalogBecause");
            OnPropertyChanged(nameof(HasItems));
            ItemsView.Refresh();
            _services.Log.Write(LogLevel.Info, $"Catalog loaded: {Items.Count} items ({catalog.StatusText}).");
        }
        catch (Exception ex)
        {
            _services.Log.Write(LogLevel.Error, "Catalog load failed.", ex);
            StatusMessage = Loc.Get("Catalog_TheCatalogCouldNotBe");
            WarningText = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool FilterItem(object item)
    {
        if (item is not CatalogItemViewModel row)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(SelectedCategory) && SelectedCategory != Loc.Get("Catalog_AllCategories") &&
            !row.Category.Equals(SelectedCategory, StringComparison.CurrentCultureIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        var query = SearchText.Trim();
        return row.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               (row.WingetId?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
               (row.Description?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false);
    }

    private void OnItemSelectionChanged(object? sender, EventArgs e)
    {
        SelectedCount = Items.Count(i => i.IsSelected);
    }
}
