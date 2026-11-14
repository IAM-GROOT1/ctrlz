using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CtrlZ.App.Services;

namespace CtrlZ.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly ThemeService _theme;
    private bool _disposed;

    public MainViewModel(AppServices services, DialogService dialogs, ThemeService theme, bool autoInstall)
    {
        _services = services;
        _theme = theme;
        Dialogs = dialogs;
        AutoInstallOnStartup = autoInstall;

        Scan = new ScanViewModel(services, dialogs, this);
        MyApps = new MyAppsViewModel(services, dialogs, this);
        AddApp = new AddAppViewModel(services, dialogs, this);
        Catalog = new CatalogViewModel(services, dialogs, this);
        Browsers = new BrowsersViewModel(services, dialogs, this);
        Install = new InstallViewModel(services, dialogs, this);
        Settings = new SettingsViewModel(services, dialogs, this, theme);
        SettingsNav = new NavItem("settings", "Settings", "\uE713", Settings);
        SettingsNavItems = [SettingsNav];

        WorkspaceNav =
        [
            new NavItem("scan", "Scan", "\uE721", Scan),
            new NavItem("apps", "My Apps", "\uE71D", MyApps),
            new NavItem("install", "Install", "\uE896", Install)
        ];
        DiscoverNav =
        [
            new NavItem("add", "Add App", "\uE710", AddApp),
            new NavItem("catalog", "Catalog", "\uE8F1", Catalog)
        ];
        BrowsersNav =
        [
            new NavItem("browsers", "Browsers", "\uE774", Browsers)
        ];

        _currentPage = autoInstall ? Install : Scan;
        SetCurrent(autoInstall ? WorkspaceNav[2] : WorkspaceNav[0]);
    }

    public DialogService Dialogs { get; }
    public ThemeService Theme => _theme;
    public string AppVersion { get; } =
        typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.1.2";
    public ScanViewModel Scan { get; }
    public MyAppsViewModel MyApps { get; }
    public AddAppViewModel AddApp { get; }
    public CatalogViewModel Catalog { get; }
    public BrowsersViewModel Browsers { get; }
    public InstallViewModel Install { get; }
    public SettingsViewModel Settings { get; }

    public ObservableCollection<NavItem> WorkspaceNav { get; }
    public ObservableCollection<NavItem> DiscoverNav { get; }
    public ObservableCollection<NavItem> BrowsersNav { get; }
    public ObservableCollection<NavItem> SettingsNavItems { get; }
    public bool AutoInstallOnStartup { get; }
    public AppState State => _services.State;

    [ObservableProperty]
    private PageViewModelBase _currentPage;

    [ObservableProperty]
    private NavItem? _currentNav;

    [ObservableProperty]
    private string _profileLabel = string.Empty;

    public IEnumerable<NavItem> AllNav => WorkspaceNav.Concat(DiscoverNav).Concat(BrowsersNav).Append(SettingsNav);

    public NavItem SettingsNav { get; }

    public async Task InitializeAsync()
    {
        _services.State.ActiveProfileChanged += OnActiveProfileChanged;
        _services.State.ProfilesChanged += OnProfilesChanged;
        OnActiveProfileChanged(null, EventArgs.Empty);
        _theme.Apply(_services.State.Settings.Theme);
        if (AutoInstallOnStartup)
        {
            // An elevated relaunch should resume immediately, not rescan the whole PC first.
            await Install.AutoStartAsync();
        }
        else
        {
            await CurrentPage.ActivateAsync();
        }
    }

    [RelayCommand]
    private async Task NavigateAsync(NavItem? item)
    {
        if (item is null)
        {
            return;
        }

        SetCurrent(item);
        try
        {
            await item.Page.ActivateAsync();
        }
        catch (Exception ex)
        {
            _services.Log.Write(Core.Logging.LogLevel.Error, $"Page activation failed for {item.Key}.", ex);
            Dialogs.Error($"This page could not be loaded: {ex.Message}");
        }
    }

    public async Task NavigateToAsync(string key)
    {
        var item = AllNav.FirstOrDefault(n => n.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
        {
            await NavigateAsync(item);
        }
    }

    [RelayCommand]
    private async Task NavigateKeyAsync(string? key)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            await NavigateToAsync(key);
        }
    }

    public System.Windows.Window? GetWindow() => System.Windows.Application.Current.MainWindow;

    public void SelectSettings()
    {
        if (SettingsNav is not null)
        {
            _ = NavigateAsync(SettingsNav);
        }
    }

    public void RefreshProfileLabel()
    {
        ProfileLabel = $"{_services.State.ActiveProfile.Name} · {_services.State.ActiveProfile.Apps.Count} apps";
    }

    private void SetCurrent(NavItem item)
    {
        foreach (var nav in AllNav)
        {
            nav.IsCurrent = nav == item;
        }

        CurrentNav = item;
        CurrentPage = item.Page;
    }

    private void OnActiveProfileChanged(object? sender, EventArgs e)
    {
        RefreshProfileLabel();
        MyApps.ReloadIfActive();
    }

    private void OnProfilesChanged(object? sender, EventArgs e)
    {
        RefreshProfileLabel();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _services.State.ActiveProfileChanged -= OnActiveProfileChanged;
        _services.State.ProfilesChanged -= OnProfilesChanged;
    }
}
