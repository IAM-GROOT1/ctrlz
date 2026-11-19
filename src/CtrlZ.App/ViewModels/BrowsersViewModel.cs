// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CtrlZ.App.Services;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Models;
using CtrlZ.Core.Services;
using Microsoft.Win32;

namespace CtrlZ.App.ViewModels;

public enum BrowserPageMode
{
    Backup,
    Restore
}

public sealed partial class BrowsersViewModel : PageViewModelBase
{
    private readonly AppServices _services;
    private readonly DialogService _dialogs;
    private readonly MainViewModel _main;
    private CancellationTokenSource? _cts;
    private bool _loaded;

    public BrowsersViewModel(AppServices services, DialogService dialogs, MainViewModel main)
        : base("Browsers", "Back up Firefox and Chromium profiles, then restore tabs, bookmarks, extensions, and settings on a clean PC.")
    {
        _services = services;
        _dialogs = dialogs;
        _main = main;
        _backupFolder = string.IsNullOrWhiteSpace(services.State.Settings.LastBackupFolder)
            ? services.Paths.DefaultBackupFolder
            : services.State.Settings.LastBackupFolder!;
        _excludedSummary = "Cache/Code Cache/GPUCache/ShaderCache, crash dumps, saved passwords, cookies";
        RestoreTargets = new ObservableCollection<BrowserItemViewModel>();
        RestoreProfiles = new ObservableCollection<RestoreProfileChoiceViewModel>();
        _archivePath = services.State.Settings.LastRestoreArchive ?? string.Empty;
    }

    public ObservableCollection<BrowserItemViewModel> Browsers { get; } = new();
    public ObservableCollection<BrowserItemViewModel> RestoreTargets { get; }
    public ObservableCollection<RestoreProfileChoiceViewModel> RestoreProfiles { get; }

    [ObservableProperty]
    private BrowserPageMode _mode = BrowserPageMode.Backup;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "Detecting Firefox and Chromium browsers…";

    [ObservableProperty]
    private string? _warningText;

    [ObservableProperty]
    private string _backupFolder;

    [ObservableProperty]
    private string? _backupPassword;

    [ObservableProperty]
    private int _selectedProfileCount;

    [ObservableProperty]
    private string _estimateText = "Select a browser and at least one profile.";

    [ObservableProperty]
    private bool _isBackingUp;

    [ObservableProperty]
    private double _backupProgress;

    [ObservableProperty]
    private string _backupStatus = "Ready to back up.";

    [ObservableProperty]
    private string _archivePath = string.Empty;

    [ObservableProperty]
    private string? _archivePassword;

    [ObservableProperty]
    private bool _isInspecting;

    [ObservableProperty]
    private BrowserArchiveInfo? _archiveInfo;

    [ObservableProperty]
    private BrowserItemViewModel? _targetBrowser;

    [ObservableProperty]
    private bool _restoreAsNewProfiles = true;

    [ObservableProperty]
    private bool _isRestoring;

    [ObservableProperty]
    private double _restoreProgress;

    [ObservableProperty]
    private string _restoreStatus = "Choose an archive to see what can be restored.";

    [ObservableProperty]
    private string _archiveSummary = string.Empty;

    [ObservableProperty]
    private int _selectedRestoreCount;

    [ObservableProperty]
    private string _excludedSummary;

    public bool IsBackupMode => Mode == BrowserPageMode.Backup;
    public bool IsRestoreMode => Mode == BrowserPageMode.Restore;

    public bool IsBackupTab
    {
        get => Mode == BrowserPageMode.Backup;
        set
        {
            if (value) { Mode = BrowserPageMode.Backup; }
            else if (Mode == BrowserPageMode.Backup) { OnPropertyChanged(); }
        }
    }

    public bool IsRestoreTab
    {
        get => Mode == BrowserPageMode.Restore;
        set
        {
            if (value) { Mode = BrowserPageMode.Restore; }
            else if (Mode == BrowserPageMode.Restore) { OnPropertyChanged(); }
        }
    }
    public bool HasBrowsers => Browsers.Count > 0;
    public bool HasNoBrowsers => Browsers.Count == 0;
    public bool ShowEmptyState => HasNoBrowsers && !IsLoading;
    public bool HasArchive => ArchiveInfo is not null;
    public bool CanBackup => !IsBackingUp && SelectedProfileCount > 0 && !string.IsNullOrWhiteSpace(BackupFolder);
    public bool CanRestore => !IsRestoring && HasArchive && TargetBrowser is not null && SelectedRestoreCount > 0 &&
                              ArchiveInfo is not null && TargetBrowser.Family == ArchiveInfo.Manifest.Family;
    public bool IsBusy => IsBackingUp || IsRestoring || IsInspecting || IsLoading;
    public bool HasWarning => !string.IsNullOrWhiteSpace(WarningText);
    public string DetectedSummary => $"{Browsers.Count} browser{(Browsers.Count == 1 ? string.Empty : "s")} detected";
    public string ArchiveContentsText => ArchiveInfo?.Manifest.Contents is { } contents
        ? string.Join(" · ", new[]
        {
            contents.Bookmarks ? "bookmarks" : null,
            contents.History ? "history" : null,
            contents.Sessions ? "tabs & sessions" : null,
            contents.Extensions ? "extensions" : null,
            contents.Preferences ? "settings" : null,
            contents.SearchEngines ? "search engines" : null,
            contents.SitePermissions ? "site permissions" : null
        }.Where(x => x is not null))
        : string.Empty;

    public override async Task ActivateAsync()
    {
        if (!_loaded)
        {
            _loaded = true;
            await DetectAsync();
            if (!string.IsNullOrWhiteSpace(ArchivePath) && File.Exists(ArchivePath))
            {
                await InspectArchiveAsync();
            }
        }
    }

    partial void OnModeChanged(BrowserPageMode value)
    {
        OnPropertyChanged(nameof(IsBackupMode));
        OnPropertyChanged(nameof(IsRestoreMode));
        OnPropertyChanged(nameof(IsBackupTab));
        OnPropertyChanged(nameof(IsRestoreTab));
    }

    partial void OnIsBackingUpChanged(bool value) => NotifyBusy();

    partial void OnIsRestoringChanged(bool value) => NotifyBusy();

    partial void OnIsInspectingChanged(bool value) => NotifyBusy();

    partial void OnIsLoadingChanged(bool value)
    {
        NotifyBusy();
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    partial void OnWarningTextChanged(string? value) => OnPropertyChanged(nameof(HasWarning));

    partial void OnSelectedProfileCountChanged(int value)
    {
        OnPropertyChanged(nameof(CanBackup));
    }

    partial void OnTargetBrowserChanged(BrowserItemViewModel? value) => OnPropertyChanged(nameof(CanRestore));

    partial void OnSelectedRestoreCountChanged(int value) => OnPropertyChanged(nameof(CanRestore));

    partial void OnArchiveInfoChanged(BrowserArchiveInfo? value)
    {
        OnPropertyChanged(nameof(HasArchive));
        OnPropertyChanged(nameof(ArchiveContentsText));
        OnPropertyChanged(nameof(CanRestore));
    }

    partial void OnArchivePathChanged(string value)
    {
        ArchiveInfo = null;
        ArchiveSummary = string.Empty;
        RestoreProfiles.Clear();
        SelectedRestoreCount = 0;
    }

    partial void OnBackupFolderChanged(string value) => OnPropertyChanged(nameof(CanBackup));

    [RelayCommand]
    private void SetMode(string? mode)
    {
        Mode = string.Equals(mode, "restore", StringComparison.OrdinalIgnoreCase)
            ? BrowserPageMode.Restore
            : BrowserPageMode.Backup;
    }

    [RelayCommand]
    private async Task RefreshBrowsersAsync() => await DetectAsync();

    [RelayCommand]
    private async Task AddCustomBrowserAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select the browser's profile root (Local State for Chromium, profiles.ini for Firefox)"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var existing = Browsers.FirstOrDefault(b =>
                string.Equals(b.UserDataPath, dialog.FolderName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.IsExpanded = true;
                _dialogs.Info("That profile folder is already in the list.");
                return;
            }

            var custom = _services.BrowserDetector.CreateCustom(dialog.FolderName);
            var item = CreateItem(custom);
            Browsers.Add(item);
            RestoreTargets.Add(item);
            item.IsExpanded = true;
            item.SetAllProfiles(true);
            _services.State.Settings.CustomBrowserUserDataPath = custom.UserDataPath;
            _services.State.SaveSettings();
            StatusMessage = $"Added {custom.DisplayName}.";
            OnPropertyChanged(nameof(HasBrowsers));
            OnPropertyChanged(nameof(HasNoBrowsers));
            OnPropertyChanged(nameof(ShowEmptyState));
            OnPropertyChanged(nameof(DetectedSummary));
            RecalculateEstimate();
        }
        catch (BrowserPathValidationException ex)
        {
            _dialogs.Error(ex.Message, "Not a browser profile folder");
        }
        catch (Exception ex)
        {
            _services.Log.Write(LogLevel.Warning, "Could not add custom browser.", ex);
            _dialogs.Error("This folder could not be added.\n\n" + ex.Message);
        }

        await Task.CompletedTask;
    }

    [RelayCommand]
    private void BrowseBackupFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose where browser backups are saved",
            InitialDirectory = Directory.Exists(BackupFolder) ? BackupFolder : null
        };
        if (dialog.ShowDialog() == true)
        {
            BackupFolder = dialog.FolderName;
        }
    }

    [RelayCommand]
    private void OpenBackupFolder()
    {
        try
        {
            Directory.CreateDirectory(BackupFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{BackupFolder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _dialogs.Error("Could not open the backup folder.\n\n" + ex.Message);
        }
    }

    [RelayCommand]
    private async Task BackupAsync()
    {
        if (!CanBackup)
        {
            return;
        }

        var targets = Browsers
            .Select(browser => (Browser: browser, Profiles: browser.SelectedProfiles.ToArray()))
            .Where(pair => pair.Profiles.Length > 0)
            .ToArray();
        if (targets.Length == 0)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(BackupPassword) && BackupPassword.Length < 4)
        {
            _dialogs.Info("Use at least 4 characters for the archive password, or leave it blank.");
            return;
        }

        try
        {
            Directory.CreateDirectory(BackupFolder);
        }
        catch (Exception ex)
        {
            _dialogs.Error("The backup folder could not be created.\n\n" + ex.Message);
            return;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsBackingUp = true;
        // Keeps the updater from prompting or applying during a backup.
        using var activity = _services.Activity.Begin("Backing up browser profiles");
        BackupProgress = 0;
        var completed = new List<BrowserBackupResult>();
        var failures = new List<string>();
        try
        {
            for (var index = 0; index < targets.Length; index++)
            {
                var target = targets[index];
                var browser = target.Browser;
                BackupStatus = $"Preparing {browser.Name}…";
                if (!await EnsureBrowserClosedAsync(browser))
                {
                    failures.Add($"{browser.Name}: skipped because it is still running.");
                    continue;
                }

                try
                {
                    var progress = new Progress<BrowserBackupProgress>(update =>
                    {
                        var overall = (index + (update.Fraction ?? 0)) / targets.Length;
                        BackupProgress = Math.Clamp(overall * 100, 0, 100);
                        BackupStatus = $"{browser.Name} · {update.Message} ({update.Processed}/{update.Total})";
                    });
                    var result = await _services.BrowserBackups.BackupAsync(
                        new BrowserBackupRequest(browser.Browser, target.Profiles, BackupFolder, NullIfEmpty(BackupPassword)),
                        progress,
                        _cts.Token);
                    completed.Add(result);
                    BackupProgress = (double)(index + 1) / targets.Length * 100;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (BrowserRunningException ex)
                {
                    failures.Add($"{browser.Name}: {ex.Message}");
                }
                catch (Exception ex)
                {
                    _services.Log.Write(LogLevel.Error, $"Browser backup failed for {browser.Name}.", ex);
                    failures.Add($"{browser.Name}: {ex.Message}");
                }
            }

            _services.State.Settings.LastBackupFolder = BackupFolder;
            _services.State.SaveSettings();
            BackupStatus = completed.Count == 0
                ? "No backups completed."
                : $"Backed up {completed.Count} browser{(completed.Count == 1 ? string.Empty : "s")}.";
            if (completed.Count > 0)
            {
                var message = string.Join(Environment.NewLine, completed.Select(r =>
                    $"{Path.GetFileName(r.ArchivePath)}  ·  {FileUtil.FormatBytes(r.ArchiveBytes)}"));
                if (failures.Count > 0)
                {
                    message += Environment.NewLine + Environment.NewLine + "Skipped:" + Environment.NewLine +
                               string.Join(Environment.NewLine, failures);
                }

                _dialogs.Info(message, "Browser backup complete");
            }
            else if (failures.Count > 0)
            {
                _dialogs.Error(string.Join(Environment.NewLine, failures), "Backup failed");
            }
        }
        catch (OperationCanceledException)
        {
            BackupStatus = "Backup cancelled. Completed archives remain in the folder.";
        }
        catch (Exception ex)
        {
            _services.Log.Write(LogLevel.Error, "Browser backup session failed.", ex);
            BackupStatus = "Backup stopped unexpectedly.";
            _dialogs.Error("The backup session stopped unexpectedly.\n\n" + ex.Message);
        }
        finally
        {
            IsBackingUp = false;
            OnPropertyChanged(nameof(CanBackup));
        }
    }

    [RelayCommand]
    private void CancelOperation()
    {
        _cts?.Cancel();
        BackupStatus = "Cancelling…";
        RestoreStatus = "Cancelling…";
    }

    [RelayCommand]
    private void BrowseArchive()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open a CTRL+Z browser backup",
            Filter = "CTRL+Z browser backup (*.zip)|*.zip|All files (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory = Directory.Exists(BackupFolder) ? BackupFolder : null
        };
        if (dialog.ShowDialog() == true)
        {
            ArchivePath = dialog.FileName;
            _ = InspectArchiveAsync();
        }
    }

    [RelayCommand]
    private async Task InspectArchiveAsync()
    {
        if (string.IsNullOrWhiteSpace(ArchivePath) || IsInspecting)
        {
            return;
        }

        IsInspecting = true;
        RestoreStatus = "Reading archive…";
        ArchiveInfo = null;
        RestoreProfiles.Clear();
        SelectedRestoreCount = 0;
        try
        {
            var info = await _services.BrowserRestore.InspectAsync(ArchivePath, NullIfEmpty(ArchivePassword));
            ArchiveInfo = info;
            foreach (var profile in info.Manifest.Profiles)
            {
                var choice = new RestoreProfileChoiceViewModel(profile);
                choice.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(RestoreProfileChoiceViewModel.IsSelected))
                    {
                        UpdateRestoreCount();
                    }
                };
                RestoreProfiles.Add(choice);
            }

            var family = info.Manifest.Family == BrowserFamily.Firefox ? "Firefox" : "Chromium";
            ArchiveSummary = $"{family} · {info.Manifest.BrowserName} {info.Manifest.BrowserVersion} · " +
                             $"{info.Manifest.CreatedUtc.ToLocalTime():MMM d, yyyy HH:mm} · " +
                             $"{info.Manifest.ProfileCount()} profile(s) · " +
                             $"{FileUtil.FormatBytes(info.Manifest.TotalBytes)}";
            SelectedRestoreCount = RestoreProfiles.Count(p => p.IsSelected);
            RestoreTargets.Clear();
            foreach (var candidate in Browsers.Where(b => b.CanSelect && b.Family == info.Manifest.Family))
            {
                RestoreTargets.Add(candidate);
            }

            var match = RestoreTargets.FirstOrDefault(b =>
                b.Id.Equals(info.Manifest.BrowserId, StringComparison.OrdinalIgnoreCase));
            TargetBrowser = match ?? RestoreTargets.FirstOrDefault();
            _services.State.Settings.LastRestoreArchive = ArchivePath;
            _services.State.SaveSettings();
            RestoreStatus = TargetBrowser is null
                ? $"Archive loaded. No {info.Manifest.Family} browser is installed. Install one, then refresh and try again."
                : info.IsEncrypted
                    ? "Archive unlocked. Choose profiles and a target browser."
                    : "Archive ready. Choose profiles and a target browser.";
            OnPropertyChanged(nameof(ArchiveContentsText));
            OnPropertyChanged(nameof(CanRestore));
        }
        catch (BrowserArchivePasswordException)
        {
            ArchiveSummary = string.Empty;
            RestoreStatus = "This archive is encrypted. Enter its password, then inspect again.";
        }
        catch (Exception ex)
        {
            _services.Log.Write(LogLevel.Warning, "Archive inspection failed.", ex);
            ArchiveSummary = string.Empty;
            RestoreStatus = "Archive could not be read.";
            _dialogs.Error("This archive could not be opened.\n\n" + ex.Message);
        }
        finally
        {
            IsInspecting = false;
            UpdateRestoreCount();
        }
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (!CanRestore || TargetBrowser is null || ArchiveInfo is null)
        {
            return;
        }

        var selected = RestoreProfiles.Where(p => p.IsSelected).Select(p => p.Profile.DirectoryName).ToArray();
        if (selected.Length == 0)
        {
            _dialogs.Info("Select at least one profile from the archive.");
            return;
        }

        if (!await EnsureBrowserClosedAsync(TargetBrowser))
        {
            return;
        }

        if (!RestoreAsNewProfiles &&
            !_dialogs.Confirm("Overwrite existing profiles",
                $"The selected profiles will be merged into {TargetBrowser.Name}'s existing profile folders. " +
                "Your current cookies and saved passwords are left alone, but bookmarks, history, sessions, and settings will be replaced.\n\n" +
                "Restore as new profiles instead if you want to keep both.",
                "Overwrite profiles", "Cancel", danger: true))
        {
            return;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsRestoring = true;
        // Keeps the updater from prompting or applying during a restore.
        using var activity = _services.Activity.Begin("Restoring browser profiles");
        RestoreProgress = 0;
        RestoreStatus = "Restoring…";
        try
        {
            var progress = new Progress<BrowserRestoreProgress>(update =>
            {
                RestoreProgress = (update.Fraction ?? 0) * 100;
                RestoreStatus = update.Message;
            });
            BrowserRestoreResult result;
            try
            {
                result = await _services.BrowserRestore.RestoreAsync(
                    new BrowserRestoreRequest(
                        ArchivePath,
                        NullIfEmpty(ArchivePassword),
                        TargetBrowser.Browser,
                        selected,
                        RestoreAsNewProfiles),
                    progress,
                    _cts.Token);
            }
            catch (BrowserRestoreConflictException ex)
            {
                var choice = _dialogs.Show("Profiles already exist",
                    $"These profiles already exist in {TargetBrowser.Name}: {string.Join(", ", ex.Profiles)}.\n\n" +
                    "Restore them as new profiles instead? This keeps the current profiles intact.",
                    "Restore as new", "Cancel");
                if (choice.Outcome != CtrlZ.App.Views.DialogOutcome.Primary)
                {
                    RestoreStatus = "Restore cancelled.";
                    return;
                }

                RestoreAsNewProfiles = true;
                result = await _services.BrowserRestore.RestoreAsync(
                    new BrowserRestoreRequest(ArchivePath, NullIfEmpty(ArchivePassword), TargetBrowser.Browser,
                        selected, true),
                    progress,
                    _cts.Token);
            }

            RestoreProgress = 100;
            RestoreStatus = $"Restored into {result.BrowserName}.";
            var warnings = result.Warnings.Count > 0
                ? Environment.NewLine + Environment.NewLine + "Notes:" + Environment.NewLine +
                  string.Join(Environment.NewLine, result.Warnings.Take(8))
                : string.Empty;
            var safety = string.IsNullOrWhiteSpace(result.SafetyBackupFolder)
                ? string.Empty
                : $"\n\nA safety copy was kept at:\n{result.SafetyBackupFolder}";
            _dialogs.Info(
                $"Restored {result.RestoredProfiles.Count} profile(s) into {result.BrowserName}.\n\n" +
                "Start the browser and check bookmarks, tabs, extensions, and settings." +
                safety + warnings,
                "Browser restore complete");
            await DetectAsync();
        }
        catch (OperationCanceledException)
        {
            RestoreStatus = "Restore cancelled.";
        }
        catch (BrowserArchivePasswordException)
        {
            RestoreStatus = "The archive password is incorrect.";
            _dialogs.Error("That password could not unlock the archive.");
        }
        catch (BrowserRunningException ex)
        {
            RestoreStatus = "Close the browser first.";
            _dialogs.Error(ex.Message);
        }
        catch (Exception ex)
        {
            _services.Log.Write(LogLevel.Error, "Browser restore failed.", ex);
            RestoreStatus = "Restore failed.";
            _dialogs.Error("The browser profile could not be restored.\n\n" + ex.Message);
        }
        finally
        {
            IsRestoring = false;
            OnPropertyChanged(nameof(CanRestore));
        }
    }

    [RelayCommand]
    private void SelectAllRestoreProfiles()
    {
        foreach (var profile in RestoreProfiles)
        {
            profile.IsSelected = true;
        }

        UpdateRestoreCount();
    }

    [RelayCommand]
    private void ClearRestoreProfiles()
    {
        foreach (var profile in RestoreProfiles)
        {
            profile.IsSelected = false;
        }

        UpdateRestoreCount();
    }

    private async Task DetectAsync()
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        WarningText = null;
        StatusMessage = "Detecting Firefox and Chromium browsers…";
        try
        {
            var detected = await _services.BrowserDetector.DetectAsync(
                _services.State.Settings.CustomBrowserUserDataPath);
            Browsers.Clear();
            RestoreTargets.Clear();
            foreach (var browser in detected)
            {
                var item = CreateItem(browser);
                Browsers.Add(item);
                if (item.CanSelect)
                {
                    RestoreTargets.Add(item);
                }
            }

            var helium = Browsers.FirstOrDefault(b => b.Id.Equals("helium", StringComparison.OrdinalIgnoreCase) && b.CanSelect);
            if (helium is not null)
            {
                helium.IsExpanded = true;
                helium.SetAllProfiles(true);
            }

            StatusMessage = Browsers.Count == 0
                ? "No Firefox or Chromium browsers found. Add a custom profile folder."
                : $"{Browsers.Count} browser{(Browsers.Count == 1 ? string.Empty : "s")} found.";
            if (Browsers.Count == 0)
            {
                WarningText = "Firefox and Chromium store profiles in different folders. For lesser-known forks, add the profile root manually.";
            }

            OnPropertyChanged(nameof(HasBrowsers));
            OnPropertyChanged(nameof(HasNoBrowsers));
            OnPropertyChanged(nameof(DetectedSummary));
            TargetBrowser = RestoreTargets.FirstOrDefault();
            RecalculateEstimate();
        }
        catch (Exception ex)
        {
            _services.Log.Write(LogLevel.Error, "Browser detection failed.", ex);
            StatusMessage = "Browser detection failed.";
            WarningText = ex.Message;
            _dialogs.Error("CTRL+Z could not detect browsers.\n\n" + ex.Message);
        }
        finally
        {
            IsLoading = false;
        }

        await Task.CompletedTask;
    }

    private BrowserItemViewModel CreateItem(DetectedBrowser browser)
    {
        var item = new BrowserItemViewModel(browser, RecalculateEstimate);
        foreach (var profile in item.Profiles)
        {
            profile.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(BrowserProfileChoiceViewModel.IsSelected))
                {
                    RecalculateEstimate();
                }
            };
        }

        return item;
    }

    private void RecalculateEstimate()
    {
        var selected = Browsers.Where(b => b.SelectedProfiles.Count > 0).ToArray();
        SelectedProfileCount = selected.Sum(b => b.SelectedProfiles.Count);
        long totalBytes = 0;
        foreach (var browser in selected)
        {
            try
            {
                var plan = BrowserBackupPlanner.Build(browser.Browser, browser.SelectedProfiles);
                var estimate = BrowserBackupPlanner.Estimate(plan);
                totalBytes += estimate.TotalBytes;
                browser.EstimatedText = $"{FileUtil.FormatBytes(estimate.TotalBytes)} · {estimate.FileCount} files";
            }
            catch
            {
                browser.EstimatedText = "Size unavailable";
            }
        }

        foreach (var browser in Browsers.Where(b => b.SelectedProfiles.Count == 0))
        {
            browser.EstimatedText = string.Empty;
        }

        EstimateText = SelectedProfileCount == 0
            ? "Select a browser and at least one profile."
            : $"{SelectedProfileCount} profile{(SelectedProfileCount == 1 ? string.Empty : "s")} · {FileUtil.FormatBytes(totalBytes)} estimated";
        OnPropertyChanged(nameof(CanBackup));
    }

    private async Task<bool> EnsureBrowserClosedAsync(BrowserItemViewModel browser)
    {
        var running = _services.BrowserProcesses.FindRunning(browser.Browser);
        if (running.Count == 0)
        {
            return true;
        }

        var choice = _dialogs.Show(
            "Close the browser",
            $"{browser.Name} is running. A backup needs exclusive-ish access to its profile files, and a restore must write them while the browser is closed.\n\n" +
            "CTRL+Z can close it for you. Unsaved work in tabs may be lost; the browser usually offers to restore tabs on next launch.",
            "Close browser",
            "Skip");
        if (choice.Outcome != CtrlZ.App.Views.DialogOutcome.Primary)
        {
            return false;
        }

        if (await _services.BrowserProcesses.CloseAsync(browser.Browser, force: false))
        {
            return true;
        }

        var force = _dialogs.Show(
            "Force close?",
            $"{browser.Name} did not close gracefully. Force-closing can lose unsaved form data, but it is the reliable way to unlock profile files.",
            "Force close",
            "Skip",
            danger: true);
        return force.Outcome == CtrlZ.App.Views.DialogOutcome.Primary &&
               await _services.BrowserProcesses.CloseAsync(browser.Browser, force: true);
    }

    private void UpdateRestoreCount()
    {
        SelectedRestoreCount = RestoreProfiles.Count(p => p.IsSelected);
        OnPropertyChanged(nameof(CanRestore));
    }

    private void NotifyBusy()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanBackup));
        OnPropertyChanged(nameof(CanRestore));
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

internal static class BackupManifestExtensions
{
    public static int ProfileCount(this BrowserBackupManifest manifest) => manifest.Profiles.Count;
}
