// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.App.Localization;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CtrlZ.App.Services;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Models;
using CtrlZ.Core.Updates;
using InstallState = CtrlZ.Core.Models.InstallState;

namespace CtrlZ.App.ViewModels;

public sealed partial class SettingsViewModel : PageViewModelBase
{
    /// <summary>Canonical public source location, surfaced in About for AGPL compliance.</summary>
    public const string SourceUrl = "https://github.com/IAM-GROOT1/ctrlz";

    private readonly AppServices _services;
    private readonly DialogService _dialogs;
    private readonly MainViewModel _main;
    private readonly ThemeService _themeService;

    public SettingsViewModel(AppServices services, DialogService dialogs, MainViewModel main, ThemeService theme)
        : base(Loc.Get("Settings_Settings"), Loc.Get("Settings_ThemeLogsAndLocalData"))
    {
        _services = services;
        _dialogs = dialogs;
        _main = main;
        _themeService = theme;
        AppVersion = AppVersionInfo.Display;
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
        ThemeMode.Light => Loc.Get("Settings_LightTheme"),
        ThemeMode.Dark => Loc.Get("Settings_DarkTheme"),
        _ => Loc.Get("Settings_FollowingWindows")
    };

    [ObservableProperty]
    private ThemeMode _theme;

    [ObservableProperty]
    private string _wingetStatus = Loc.Get("Settings_CheckingWinget");

    [ObservableProperty]
    private string _logText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _autoCheckForUpdates;

    [ObservableProperty]
    private UpdateChannel _channel;

    [ObservableProperty]
    private bool _isCheckingForUpdates;

    [ObservableProperty]
    private bool _showInstallPreview;
    [ObservableProperty]
    private bool _includeNotesInReports;

    public string CurrentVersionText => Loc.Format("Settings_CTRLZ", AppVersion);

    public bool IsStableChannel => Channel == UpdateChannel.Stable;

    public bool IsPreReleaseChannel => Channel == UpdateChannel.PreRelease;

    public string ChannelDescription => Channel == UpdateChannel.Stable
        ? Loc.Get("Settings_OnlyFullReleasesAreOffered")
        : Loc.Get("Settings_FullReleasesAndPreReleases");

    public string LastUpdateCheckText => _services.State.Settings.LastUpdateCheckUtc is { } last
        ? Loc.Format("Settings_LastChecked", last.ToLocalTime())
        : Loc.Get("Settings_NotCheckedYet");

    public bool CanCheckForUpdates => !IsCheckingForUpdates;

    public bool IsSystemTheme => Theme == ThemeMode.System;
    public bool IsLightTheme => Theme == ThemeMode.Light;
    public bool IsDarkTheme => Theme == ThemeMode.Dark;
    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);

    /// <summary>"Windows default" and each language CTRL+Z ships.</summary>
    public IReadOnlyList<LanguageOption> Languages { get; } = AppLanguage.Options();

    private LanguageOption? _selectedLanguage;

    /// <summary>The saved choice; changing it saves it and takes effect after a restart.</summary>
    public LanguageOption? SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedLanguage))
            {
                return;
            }

            var changed = _selectedLanguage is not null;
            _selectedLanguage = value;
            OnPropertyChanged();
            if (!changed)
            {
                return;
            }

            _services.State.Settings.Language = value.Code.Length == 0 ? null : value.Code;
            _services.State.SaveSettings();
            StatusMessage = Loc.Format("Settings_LanguageRestart", value.Name);
        }
    }

    public override async Task ActivateAsync()
    {
        var saved = AppLanguage.Normalize(_services.State.Settings.Language) ?? string.Empty;
        _selectedLanguage = null;
        SelectedLanguage = Languages.FirstOrDefault(option => option.Code == saved) ?? Languages[0];
        Theme = _services.State.Settings.Theme;
        AutoCheckForUpdates = _services.State.Settings.AutoCheckForUpdates;
        Channel = _services.State.Settings.UpdateChannel;
        ShowInstallPreview = _services.State.Settings.ShowInstallPreview;
        CreatePortableShortcuts = _services.State.Settings.CreatePortableShortcuts;
        IncludeNotesInReports = _services.State.Settings.IncludeNotesInReports;
        OnPropertyChanged(nameof(LastUpdateCheckText));
        LoadLogs();
        await CheckWingetAsync();
    }

    partial void OnChannelChanged(UpdateChannel value)
    {
        OnPropertyChanged(nameof(IsStableChannel));
        OnPropertyChanged(nameof(IsPreReleaseChannel));
        OnPropertyChanged(nameof(ChannelDescription));
    }

    partial void OnAutoCheckForUpdatesChanged(bool value)
    {
        _services.State.Settings.AutoCheckForUpdates = value;
        _services.State.SaveSettings();
    }

    partial void OnIsCheckingForUpdatesChanged(bool value) =>
        OnPropertyChanged(nameof(CanCheckForUpdates));

    partial void OnIncludeNotesInReportsChanged(bool value)
    {
        _services.State.Settings.IncludeNotesInReports = value;
        _services.State.SaveSettings();
    }

    partial void OnShowInstallPreviewChanged(bool value)
    {
        _services.State.Settings.ShowInstallPreview = value;
        _services.State.SaveSettings();
    }

    [ObservableProperty]
    private bool _createPortableShortcuts = true;

    partial void OnCreatePortableShortcutsChanged(bool value)
    {
        _services.State.Settings.CreatePortableShortcuts = value;
        _services.State.SaveSettings();
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
        StatusMessage = Loc.Format("Settings_Theme2", ThemeDescription);
    }

    [RelayCommand]
    private void SetUpdateChannel(string? channel)
    {
        Channel = string.Equals(channel, "prerelease", StringComparison.OrdinalIgnoreCase)
            ? UpdateChannel.PreRelease
            : UpdateChannel.Stable;

        _services.State.Settings.UpdateChannel = Channel;
        _services.State.SaveSettings();
        StatusMessage = Loc.Format("Settings_UpdateChannel", Channel);
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (IsCheckingForUpdates)
        {
            return;
        }

        IsCheckingForUpdates = true;
        StatusMessage = Loc.Get("Settings_CheckingForUpdates");
        try
        {
            await _main.Updates.CheckManuallyAsync();
        }
        finally
        {
            IsCheckingForUpdates = false;
            OnPropertyChanged(nameof(LastUpdateCheckText));
        }
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
        StatusMessage = Loc.Get("Settings_LogRefreshed");
    }

    [RelayCommand]
    private async Task CheckWingetAsync()
    {
        WingetStatus = Loc.Get("Settings_CheckingWinget");
        try
        {
            var available = await _services.Winget.IsAvailableAsync();
            WingetStatus = available
                ? Loc.Format("Settings_WingetReady", _services.Winget.Version)
                : Loc.Get("Settings_WingetNotFoundInstallApp");
        }
        catch (Exception ex)
        {
            WingetStatus = Loc.Get("Settings_WingetCheckFailed") + ex.Message;
        }
    }

    [RelayCommand]
    private async Task UpdateWingetAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (!_dialogs.Confirm(Loc.Get("Settings_UpdateAppInstaller"),
                Loc.Get("Settings_CTRLZWillAskMicrosoft"),
                Loc.Get("Settings_UpdateWinget2"), Loc.Get("Settings_Cancel")))
        {
            return;
        }

        IsBusy = true;
        StatusMessage = Loc.Get("Settings_CheckingWinget");
        try
        {
            var ready = await _services.WingetBootstrap.EnsureReadyAsync(message => StatusMessage = message);
            WingetStatus = ready ? Loc.Format("Settings_WingetReady", _services.Winget.Version) : Loc.Get("Settings_WingetIsNotAvailable");
            StatusMessage = ready ? Loc.Get("Settings_WingetIsReady") : Loc.Get("Settings_WingetCouldNotBeInstalled");
            _services.Winget.ResetDiscovery();
            await CheckWingetAsync();
        }
        catch (Exception ex)
        {
            _services.Log.Write(LogLevel.Error, "Manual winget update failed.", ex);
            StatusMessage = Loc.Get("Settings_UpdateFailed") + ex.Message;
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
            System.Windows.Clipboard.SetText(DiagnosticsBundle.Summary(DiagnosticsFacts()));
            StatusMessage = Loc.Get("Settings_DiagnosticsSummaryCopiedToThe");
        }
        catch (Exception ex)
        {
            _dialogs.Error(Loc.Get("Settings_CouldNotCopyDiagnostics") + ex.Message);
        }
    }

    [RelayCommand]
    private Task ShowWelcomeAsync() => _main.ShowWelcomeAsync();

    [ObservableProperty]
    private bool _isSavingDiagnostics;

    /// <summary>Writes a diagnostics ZIP where the user chooses. Nothing is sent anywhere.</summary>
    [RelayCommand]
    private async Task SaveDiagnosticsAsync()
    {
        if (IsSavingDiagnostics)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.Get("Settings_SaveDiagnosticsTitle"),
            Filter = Loc.Get("Settings_DiagnosticsFilter"),
            FileName = $"ctrlz-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IsSavingDiagnostics = true;
        StatusMessage = Loc.Get("Settings_SavingDiagnostics");
        try
        {
            var facts = DiagnosticsFacts();
            var path = dialog.FileName;
            await Task.Run(() => DiagnosticsBundle.Write(path, facts, LogsFolder, PathPrivacy.Current));
            _services.Log.Write(LogLevel.Info, "Diagnostics bundle saved on request.");
            StatusMessage = Loc.Get("Settings_DiagnosticsSavedNothingWasSent");
            OpenPath(path);
        }
        catch (Exception ex)
        {
            _services.Log.Write(LogLevel.Error, "Diagnostics bundle failed: " + LogPrivacy.Describe(ex));
            _dialogs.Error(Loc.Get("Settings_CouldNotSaveDiagnostics") + ex.Message);
            StatusMessage = null;
        }
        finally
        {
            IsSavingDiagnostics = false;
        }
    }

    private DiagnosticsInput DiagnosticsFacts()
    {
        InstallState? lastInstall = null;
        try
        {
            lastInstall = _services.InstallStates.Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // A missing or unreadable state file only means the summary has no last run.
        }

        return new DiagnosticsInput
        {
            AppVersion = AppVersion,
            DataLocation = DataLocationKind(),
            WingetStatus = WingetStatus,
            Settings = _services.State.Settings,
            ProfileCount = _services.State.Profiles.Count,
            ActiveProfile = _services.State.ActiveProfile,
            LastInstall = lastInstall
        };
    }

    private static string DataLocationKind()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CTRLZ_DATA_DIR")))
        {
            return Loc.Get("Settings_CustomFolderCTRLZDATADIR");
        }

        var exeFolder = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        return File.Exists(Path.Combine(exeFolder, "portable.flag")) ? "portable" : "installed";
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
            _dialogs.Info(Loc.Get("Settings_CTRLZIsReleasedUnder") +
                Loc.Format("Settings_AGPL30OnlyThe", SourceUrl),
                Loc.Get("Settings_License"));
        }
    }

    [RelayCommand]
    private void OpenSourceCode()
    {
        try
        {
            Process.Start(new ProcessStartInfo(SourceUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _dialogs.Error(Loc.Get("Settings_CouldNotOpenTheSource") + ex.Message);
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
            LogText = Loc.Get("Settings_CouldNotReadTheLog") + ex.Message;
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
                _dialogs.Info(Loc.Get("Settings_ThatFolderDoesNotExist"));
            }
        }
        catch (Exception ex)
        {
            _dialogs.Error(Loc.Get("Settings_CouldNotOpenThatLocation") + ex.Message);
        }
    }
}
