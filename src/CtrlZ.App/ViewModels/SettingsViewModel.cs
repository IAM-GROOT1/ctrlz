using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CtrlZ.App.Services;
using CtrlZ.Core.Licensing;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Models;

namespace CtrlZ.App.ViewModels;

public sealed partial class SettingsViewModel : PageViewModelBase
{
    private readonly AppServices _services;
    private readonly DialogService _dialogs;
    private readonly MainViewModel _main;
    private readonly ThemeService _themeService;

    public SettingsViewModel(AppServices services, DialogService dialogs, MainViewModel main, ThemeService theme)
        : base("Settings", "Theme, logs, and local data. Everything stays on this PC unless you export it.")
    {
        _services = services;
        _dialogs = dialogs;
        _main = main;
        _themeService = theme;
        AppVersion = typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.1.3";
        DataFolder = services.Paths.Root;
        LogsFolder = services.Paths.Logs;
        ProfilesFolder = services.Paths.Profiles;
        _licenseServerUrl = services.State.Settings.LicenseServerUrl ?? string.Empty;
        _proPurchaseUrl = services.State.Settings.ProPurchaseUrl ?? string.Empty;
        _licenseGraceDays = services.State.Settings.LicenseGraceDays;
        services.License.LicenseChanged += OnLicenseChanged;
        RefreshLicense();
    }

    public string AppVersion { get; }
    public string DataFolder { get; }
    public string LogsFolder { get; }
    public string ProfilesFolder { get; }
    public string LogFile => _services.Log.LogFilePath;
    public string ThemeDescription => Theme switch
    {
        ThemeMode.Light => "Light theme",
        ThemeMode.Dark => "Dark theme",
        _ => "Following Windows"
    };

    [ObservableProperty]
    private ThemeMode _theme;

    [ObservableProperty]
    private string _wingetStatus = "Checking winget…";

    [ObservableProperty]
    private string _logText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string _licenseKey = string.Empty;

    [ObservableProperty]
    private string _licenseServerUrl;

    [ObservableProperty]
    private string _proPurchaseUrl;

    [ObservableProperty]
    private int _licenseGraceDays;

    [ObservableProperty]
    private string _licenseStatusTitle = "No Pro license";

    [ObservableProperty]
    private string _licenseDetailText = string.Empty;

    [ObservableProperty]
    private string _licenseLastCheckText = "No license check has been made.";

    [ObservableProperty]
    private string _licenseModuleText = "Pro module not installed";

    [ObservableProperty]
    private bool _isLicenseBusy;

    public bool IsSystemTheme => Theme == ThemeMode.System;
    public bool IsLightTheme => Theme == ThemeMode.Light;
    public bool IsDarkTheme => Theme == ThemeMode.Dark;
    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);
    public bool HasLicense => _services.License.Snapshot.Status != LicenseStatus.Missing;
    public bool IsProActive => _services.License.Snapshot.IsProEnabled;

    public override async Task ActivateAsync()
    {
        Theme = _services.State.Settings.Theme;
        LicenseServerUrl = _services.State.Settings.LicenseServerUrl ?? string.Empty;
        ProPurchaseUrl = _services.State.Settings.ProPurchaseUrl ?? string.Empty;
        LicenseGraceDays = _services.State.Settings.LicenseGraceDays;
        RefreshLicense();
        LoadLogs();
        await CheckWingetAsync();
    }

    partial void OnThemeChanged(ThemeMode value)
    {
        OnPropertyChanged(nameof(IsSystemTheme));
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(IsDarkTheme));
        OnPropertyChanged(nameof(ThemeDescription));
    }

    partial void OnStatusMessageChanged(string? value) => OnPropertyChanged(nameof(HasStatus));

    [RelayCommand]
    private void SetTheme(string? mode)
    {
        Theme = mode?.ToLowerInvariant() switch
        {
            "light" => ThemeMode.Light,
            "dark" => ThemeMode.Dark,
            _ => ThemeMode.System
        };
        _services.State.Settings.Theme = Theme;
        _services.State.SaveSettings();
        _themeService.Apply(Theme);
        StatusMessage = $"Theme: {ThemeDescription}.";
    }

    [RelayCommand]
    private void OpenLogsFolder() => OpenPath(LogsFolder);

    [RelayCommand]
    private void OpenLogFile() => OpenPath(File.Exists(LogFile) ? LogFile : LogsFolder);

    [RelayCommand]
    private void OpenDataFolder() => OpenPath(DataFolder);

    [RelayCommand]
    private void OpenProfilesFolder() => OpenPath(ProfilesFolder);

    [RelayCommand]
    private void RefreshLog()
    {
        LoadLogs();
        StatusMessage = "Log refreshed.";
    }

    [RelayCommand]
    private async Task ActivateLicenseAsync()
    {
        if (IsLicenseBusy)
        {
            return;
        }

        SaveLicenseSettingsInternal();
        IsLicenseBusy = true;
        StatusMessage = "Activating license…";
        try
        {
            var snapshot = await _services.License.ActivateAsync(LicenseKey);
            LicenseKey = string.Empty;
            StatusMessage = snapshot.IsProEnabled
                ? "Pro license activated."
                : "The server returned a license that is not active.";
            RefreshLicense();
        }
        catch (LicenseException ex)
        {
            StatusMessage = ex.Message;
            _dialogs.Error(ex.Message, "License activation");
        }
        catch (Exception ex)
        {
            _services.Log.Write(LogLevel.Error, "License activation failed.", ex);
            StatusMessage = "License activation failed.";
            _dialogs.Error("License activation failed.\n\n" + ex.Message, "License activation");
        }
        finally
        {
            IsLicenseBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeactivateLicenseAsync()
    {
        if (!HasLicense || IsLicenseBusy)
        {
            return;
        }

        if (!_dialogs.Confirm(
                "Deactivate this PC?",
                "This frees the license for another machine. Pro features will be unavailable here until the license is activated again.",
                "Deactivate", "Cancel"))
        {
            return;
        }

        IsLicenseBusy = true;
        StatusMessage = "Deactivating license…";
        try
        {
            await _services.License.DeactivateAsync();
            StatusMessage = "License deactivated on this PC.";
            RefreshLicense();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            _dialogs.Error(ex.Message, "License deactivation");
        }
        finally
        {
            IsLicenseBusy = false;
        }
    }

    [RelayCommand]
    private void SaveLicenseSettings()
    {
        SaveLicenseSettingsInternal();
        StatusMessage = "License settings saved.";
        RefreshLicense();
    }

    [RelayCommand]
    private void OpenPurchasePage()
    {
        try
        {
            _services.License.OpenPurchasePage();
        }
        catch (Exception ex)
        {
            _dialogs.Error("The Pro purchase page could not be opened.\n\n" + ex.Message);
        }
    }

    [RelayCommand]
    private async Task OpenProPageAsync() => await _main.NavigateToAsync("pro");

    [RelayCommand]
    private async Task CheckWingetAsync()
    {
        WingetStatus = "Checking winget…";
        try
        {
            var available = await _services.Winget.IsAvailableAsync();
            WingetStatus = available
                ? $"winget {_services.Winget.Version} · ready"
                : "winget not found · install App Installer when you start an install";
        }
        catch (Exception ex)
        {
            WingetStatus = "winget check failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task UpdateWingetAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (!_dialogs.Confirm("Update App Installer",
                "CTRL+Z will ask Microsoft's package manager to install or update App Installer (winget). This may trigger a UAC prompt.",
                "Update winget", "Cancel"))
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Checking winget…";
        try
        {
            var ready = await _services.WingetBootstrap.EnsureReadyAsync(message => StatusMessage = message);
            WingetStatus = ready ? $"winget {_services.Winget.Version} · ready" : "winget is not available";
            StatusMessage = ready ? "winget is ready." : "winget could not be installed.";
            _services.Winget.ResetDiscovery();
            await CheckWingetAsync();
        }
        catch (Exception ex)
        {
            _services.Log.Write(LogLevel.Error, "Manual winget update failed.", ex);
            StatusMessage = "Update failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CopyDiagnostics()
    {
        try
        {
            var text = $"CTRL+Z {AppVersion}\nWindows {Environment.OSVersion.VersionString}\n" +
                       $"Data: {DataFolder}\nwinget: {WingetStatus}\nLog: {LogFile}\n";
            System.Windows.Clipboard.SetText(text);
            StatusMessage = "Diagnostics copied to the clipboard.";
        }
        catch (Exception ex)
        {
            _dialogs.Error("Could not copy diagnostics: " + ex.Message);
        }
    }

    [RelayCommand]
    private void OpenLicense()
    {
        var license = Path.Combine(AppContext.BaseDirectory, "LICENSE");
        if (File.Exists(license))
        {
            OpenPath(license);
        }
        else
        {
            _dialogs.Info("CTRL+Z is released under the MIT License. See the LICENSE file in the project repository.",
                "License");
        }
    }

    private void RefreshLicense()
    {
        var snapshot = _services.License.Snapshot;
        LicenseStatusTitle = snapshot.Status switch
        {
            LicenseStatus.Active => "Pro is active",
            LicenseStatus.OfflineGrace => "Pro is active in offline grace",
            LicenseStatus.MachineMismatch => "License belongs to another PC",
            LicenseStatus.Expired => "License needs renewal",
            LicenseStatus.Invalid => "License is not valid",
            _ => "No Pro license"
        };
        LicenseDetailText = snapshot.Message;
        LicenseLastCheckText = snapshot.LastCheckUtc is { } last
            ? $"Last check: {last.ToLocalTime():g}"
            : "No license check has been made.";
        LicenseModuleText = _services.ProModule.IsLoaded
            ? $"{_services.ProModule.Module!.DisplayName} {_services.ProModule.Module.Version} loaded"
            : "Pro module not installed";
        OnPropertyChanged(nameof(HasLicense));
        OnPropertyChanged(nameof(IsProActive));
    }

    private void SaveLicenseSettingsInternal()
    {
        var settings = _services.State.Settings;
        settings.LicenseServerUrl = string.IsNullOrWhiteSpace(LicenseServerUrl)
            ? null
            : LicenseServerUrl.Trim();
        settings.ProPurchaseUrl = string.IsNullOrWhiteSpace(ProPurchaseUrl)
            ? null
            : ProPurchaseUrl.Trim();
        settings.LicenseGraceDays = Math.Clamp(LicenseGraceDays, 1, 365);
        _services.State.SaveSettings();
    }

    private void OnLicenseChanged(object? sender, EventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            RefreshLicense();
        }
        else
        {
            dispatcher.BeginInvoke(RefreshLicense);
        }
    }

    private void LoadLogs()
    {
        try
        {
            var lines = _services.Log.Tail(250);
            LogText = string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex)
        {
            LogText = "Could not read the log: " + ex.Message;
        }
    }

    private void OpenPath(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            else if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            else
            {
                _dialogs.Info("That folder does not exist yet.");
            }
        }
        catch (Exception ex)
        {
            _dialogs.Error("Could not open that location.\n\n" + ex.Message);
        }
    }
}
