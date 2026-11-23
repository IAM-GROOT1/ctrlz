// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.App.Localization;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CtrlZ.App.Services;
using CtrlZ.Core.Updates;

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

        Updates = new UpdateCoordinator(services, dialogs, () => System.Windows.Application.Current?.MainWindow);

        Scan = new ScanViewModel(services, dialogs, this);
        MyApps = new MyAppsViewModel(services, dialogs, this);
        Readiness = new ReadinessViewModel(services, dialogs, this);
        AddApp = new AddAppViewModel(services, dialogs, this);
        Catalog = new CatalogViewModel(services, dialogs, this);
        Browsers = new BrowsersViewModel(services, dialogs, this);
        Install = new InstallViewModel(services, dialogs, this);
        AppUpdates = new AppUpdatesViewModel(services, dialogs);
        Tweaks = new TweaksViewModel(services, dialogs);
        Settings =new SettingsViewModel(services, dialogs, this, theme);
        SettingsNav = new NavItem("settings", Loc.Get("Main_Settings"), "\uE713", Settings);
        SettingsNavItems = [SettingsNav];

        WorkspaceNav =
        [
            new NavItem("scan", Loc.Get("Main_Scan"), "\uE721", Scan),
            new NavItem("apps", Loc.Get("Main_MyApps"), "\uE71D", MyApps),
            new NavItem("ready", Loc.Get("Main_ReadyToWipe"), "\uE73E", Readiness),
            new NavItem("install", Loc.Get("Main_Install"), "\uE896", Install),
            new NavItem("updates", Loc.Get("Main_Updates"), "\uE895", AppUpdates),
            new NavItem("tweaks", Loc.Get("Main_Tweaks"), "\uE771", Tweaks)
        ];
        DiscoverNav =
        [
            new NavItem("add", Loc.Get("Main_AddApp"), "\uE710", AddApp),
            new NavItem("catalog", Loc.Get("Main_Catalog"), "\uE8F1", Catalog)
        ];
        BrowsersNav =
        [
            new NavItem("browsers", Loc.Get("Main_Browsers2"), "\uE774", Browsers)
        ];

        _currentPage = autoInstall ? Install : Scan;
        SetCurrent(autoInstall ? WorkspaceNav[3] : WorkspaceNav[0]);
    }

    public DialogService Dialogs { get; }
    public ThemeService Theme => _theme;
    public string AppVersion { get; } = AppVersionInfo.Display;
    public ScanViewModel Scan { get; }
    public MyAppsViewModel MyApps { get; }
    public ReadinessViewModel Readiness { get; }
    public AddAppViewModel AddApp { get; }
    public CatalogViewModel Catalog { get; }
    public BrowsersViewModel Browsers { get; }
    public InstallViewModel Install { get; }

    /// <summary>The Updates page: app updates through winget (not CTRL+Z's own updater).</summary>
    public AppUpdatesViewModel AppUpdates { get; }
    public TweaksViewModel Tweaks { get; }
    public SettingsViewModel Settings { get; }
    public UpdateCoordinator Updates { get; }

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

        await RunOnboardingAsync();

        // Fire-and-forget: the update check must never delay the first paint.
        _ = Updates.RunStartupCheckAsync();
    }

    private async Task RunOnboardingAsync()
    {
        var settings = _services.State.Settings;
        var decision = OnboardingPolicy.Decide(settings, _services.State.Profiles.Count,
            _services.State.ActiveAppCount, AutoInstallOnStartup);
        if (decision == OnboardingDecision.None)
        {
            return;
        }

        settings.HasCompletedOnboarding = true;
        _services.State.SaveSettings();
        if (decision == OnboardingDecision.Show)
        {
            await ShowWelcomeAsync();
        }
    }

    /// <summary>Shows the first-run guide and opens the page the user picked.</summary>
    public async Task ShowWelcomeAsync()
    {
        switch (Dialogs.ShowWelcome())
        {
            case Views.WelcomeChoice.Scan:
                await NavigateToAsync("scan");
                break;
            case Views.WelcomeChoice.FreshInstall:
                await NavigateToAsync("apps");
                break;
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
            Dialogs.Error(Loc.Format("Main_ThisPageCouldNotBe", ex.Message));
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
