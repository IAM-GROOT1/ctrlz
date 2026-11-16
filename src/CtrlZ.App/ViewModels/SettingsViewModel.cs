// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CtrlZ.App.Services;
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

    public bool IsSystemTheme => Theme == ThemeMode.System;
    public bool IsLightTheme => Theme == ThemeMode.Light;
    public bool IsDarkTheme => Theme == ThemeMode.Dark;
    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);

    public override async Task ActivateAsync()
    {
        Theme = _services.State.Settings.Theme;
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
