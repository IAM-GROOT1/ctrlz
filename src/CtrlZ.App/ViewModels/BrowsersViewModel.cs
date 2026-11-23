// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.App.Localization;
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
        : base(Loc.Get("Browsers_Browsers2"), Loc.Get("Browsers_BackUpFirefoxAndChromium"))
    {
        _services = services;
        _dialogs = dialogs;
        _main = main;
        _backupFolder = string.IsNullOrWhiteSpace(services.State.Settings.LastBackupFolder)
            ? services.Paths.DefaultBackupFolder
            : services.State.Settings.LastBackupFolder!;
        _excludedSummary = Loc.Get("Browsers_CacheCodeCacheGPUCacheShaderCache");
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
    private string _statusMessage = Loc.Get("Browsers_DetectingFirefoxAndChromiumBrowsers");

    [ObservableProperty]
    private string? _warningText;

    [ObservableProperty]
    private string _backupFolder;

    [ObservableProperty]
    private string? _backupPassword;

    [ObservableProperty]
    private int _selectedProfileCount;

    [ObservableProperty]
    private string _estimateText = Loc.Get("Browsers_SelectABrowserAndAt");

    [ObservableProperty]
    private bool _isBackingUp;

    [ObservableProperty]
    private double _backupProgress;

    [ObservableProperty]
    private string _backupStatus = Loc.Get("Browsers_ReadyToBackUp");

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
    private string _restoreStatus = Loc.Get("Browsers_ChooseAnArchiveToSee");

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
    public string DetectedSummary => Loc.Plural("Browsers_DetectedCount", Browsers.Count);
    public string ArchiveContentsText => ArchiveInfo?.Manifest.Contents is { } contents
        ? string.Join(" · ", new[]
        {
            contents.Bookmarks ? "bookmarks" : null,
            contents.History ? "history" : null,
            contents.Sessions ? Loc.Get("Browsers_TabsSessions") : null,
            contents.Extensions ? "extensions" : null,
            contents.Preferences ? "settings" : null,
            contents.SearchEngines ? Loc.Get("Browsers_SearchEngines") : null,
            contents.SitePermissions ? Loc.Get("Browsers_SitePermissions") : null
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

    /// <summary>
    /// Offers only browsers of the archive's engine, prefers the archive's own browser, and notes when
    /// that browser is missing. Run after inspecting and again after a refresh, so a browser installed
    /// in between shows up.
    /// </summary>
    private void ApplyArchiveTargets(BrowserBackupManifest manifest)
    {
        RestoreTargets.Clear();
        foreach (var candidate in Browsers.Where(b => b.CanSelect && b.Family == manifest.Family))
        {
            RestoreTargets.Add(candidate);
        }

        var match = RestoreTargets.FirstOrDefault(b => b.Id.Equals(manifest.BrowserId, StringComparison.OrdinalIgnoreCase));
        TargetBrowser = match ?? RestoreTargets.FirstOrDefault();
        MissingBrowser = BrowserInstallHelp.For(manifest, Browsers.Where(b => b.CanSelect).Select(b => b.Id));
    }

    /// <summary>Set when the archive's own browser is not installed here; drives the install offer.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMissingBrowser), nameof(MissingBrowserText), nameof(InstallMissingBrowserLabel),
        nameof(MissingBrowserInstallable), nameof(CanInstallMissingBrowser))]
    private MissingBrowserAdvice? _missingBrowser;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstallMissingBrowser))]
    private bool _isInstallingBrowser;

    public bool HasMissingBrowser => MissingBrowser is not null;

    public bool MissingBrowserInstallable => MissingBrowser?.CanInstall == true;

    public bool CanInstallMissingBrowser => MissingBrowserInstallable && !IsInstallingBrowser;

    public string MissingBrowserText => MissingBrowser is not { } missing
        ? string.Empty
        : missing.CanInstall
            ? Loc.Format("Browsers_ThisBackupIsFromWhich", missing.BrowserName, missing.NextSteps)
            : Loc.Format("Browsers_ThisBackupIsFromWhich2", missing.BrowserName, missing.NextSteps);

    public string InstallMissingBrowserLabel => MissingBrowser is { } missing ? Loc.Format("Browsers_Install", missing.BrowserName) : string.Empty;

    /// <summary>Installs the archive's browser with winget, then explains the first-start step.</summary>
    [RelayCommand]
    private async Task InstallMissingBrowserAsync()
    {
        if (MissingBrowser is not { CanInstall: true } missing || IsInstallingBrowser)
        {
            return;
        }

        if (!_dialogs.Confirm(Loc.Format("Browsers_Install", missing.BrowserName),
                Loc.Format("Browsers_CTRLZWillInstallWith", missing.BrowserName, missing.WingetId) +
                Loc.Get("Browsers_AfterItInstalls") + missing.NextSteps,
                Loc.Format("Browsers_Install", missing.BrowserName), Loc.Get("Browsers_Cancel2")))
        {
            return;
        }

        IsInstallingBrowser = true;
        RestoreStatus = Loc.Format("Browsers_Installing", missing.BrowserName);
        try
        {
            if (!await _services.Winget.IsAvailableAsync())
            {
                _dialogs.Error(Loc.Get("Browsers_WingetIsNotAvailableOn"));
                RestoreStatus = Loc.Get("Browsers_WingetIsNotAvailable");
                return;
            }

            var result = await _services.Winget.InstallAsync(missing.WingetId!, "winget", null);
            _services.Log.Write(LogLevel.Info, $"Installed {missing.BrowserName} for a restore: exit code {result.ExitCode}.");
            if (result.ExitCode == 0)
            {
                RestoreStatus = Loc.Format("Browsers_IsInstalled", missing.BrowserName, missing.NextSteps);
                _dialogs.Info(Loc.Format("Browsers_IsInstalled2", missing.BrowserName, missing.NextSteps), Loc.Get("Browsers_AlmostReadyToRestore"));
            }
            else
            {
                RestoreStatus = Loc.Format("Browsers_CouldNotBeInstalledWinget", missing.BrowserName, result.ExitCode);
                _dialogs.Error(Loc.Format("Browsers_CouldNotBeInstalledWinget2", missing.BrowserName, result.ExitCode));
            }
        }
        catch (Exception ex)
        {
            _services.Log.Write(LogLevel.Warning, $"Browser install failed: {LogPrivacy.Describe(ex)}");
            RestoreStatus = Loc.Format("Browsers_CouldNotBeInstalled", missing.BrowserName);
            _dialogs.Error(Loc.Format("Browsers_CouldNotBeInstalled2", missing.BrowserName) + ex.Message);
        }
        finally
        {
            IsInstallingBrowser = false;
        }
    }

    partial void OnArchiveInfoChanged(BrowserArchiveInfo? value)
    {
        if (value is null)
        {
            MissingBrowser = null;
        }

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
            Title = Loc.Get("Browsers_SelectTheBrowserSProfile")
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
                _dialogs.Info(Loc.Get("Browsers_ThatProfileFolderIsAlready"));
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
            StatusMessage = Loc.Format("Browsers_Added", custom.DisplayName);
            OnPropertyChanged(nameof(HasBrowsers));
            OnPropertyChanged(nameof(HasNoBrowsers));
            OnPropertyChanged(nameof(ShowEmptyState));
            OnPropertyChanged(nameof(DetectedSummary));
            RecalculateEstimate();
        }
        catch (BrowserPathValidationException ex)
        {
            _dialogs.Error(ex.Message, Loc.Get("Browsers_NotABrowserProfileFolder"));
        }
        catch (Exception ex)
        {
            _services.Log.Write(LogLevel.Warning, "Could not add custom browser.", ex);
            _dialogs.Error(Loc.Get("Browsers_ThisFolderCouldNotBe") + ex.Message);
        }

        await Task.CompletedTask;
    }

    [RelayCommand]
    private void BrowseBackupFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = Loc.Get("Browsers_ChooseWhereBrowserBackupsAre"),
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
            _dialogs.Error(Loc.Get("Browsers_CouldNotOpenTheBackup") + ex.Message);
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
            _dialogs.Info(Loc.Get("Browsers_UseAtLeast4Characters"));
            return;
        }

        try
        {
            Directory.CreateDirectory(BackupFolder);
        }
        catch (Exception ex)
        {
            _dialogs.Error(Loc.Get("Browsers_TheBackupFolderCouldNot") + ex.Message);
            return;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsBackingUp = true;
        // Keeps the updater from prompting or applying during a backup.
        using var activity = _services.Activity.Begin(Loc.Get("Browsers_BackingUpBrowserProfiles"));
        BackupProgress = 0;
        var completed = new List<BrowserBackupResult>();
        var failures = new List<string>();
        try
        {
            for (var index = 0; index < targets.Length; index++)
            {
                var target = targets[index];
                var browser = target.Browser;
                BackupStatus = Loc.Format("Browsers_Preparing", browser.Name);
                if (!await EnsureBrowserClosedAsync(browser))
                {
                    failures.Add(Loc.Format("Browsers_SkippedBecauseItIsStill", browser.Name));
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
                ? Loc.Get("Browsers_NoBackupsCompleted")
                : Loc.Plural("Browsers_BackedUpCount", completed.Count);
            if (completed.Count > 0)
            {
                var message = string.Join(Environment.NewLine, completed.Select(r =>
                    $"{Path.GetFileName(r.ArchivePath)}  ·  {FileUtil.FormatBytes(r.ArchiveBytes)}"));
                if (failures.Count > 0)
                {
                    message += Environment.NewLine + Environment.NewLine + "Skipped:" + Environment.NewLine +
                               string.Join(Environment.NewLine, failures);
                }

                _dialogs.Info(message, Loc.Get("Browsers_BrowserBackupComplete"));
            }
            else if (failures.Count > 0)
            {
                _dialogs.Error(string.Join(Environment.NewLine, failures), Loc.Get("Browsers_BackupFailed"));
            }
        }
        catch (OperationCanceledException)
        {
            BackupStatus = Loc.Get("Browsers_BackupCancelledCompletedArchivesRemain");
        }
        catch (Exception ex)
        {
            _services.Log.Write(LogLevel.Error, "Browser backup session failed.", ex);
            BackupStatus = Loc.Get("Browsers_BackupStoppedUnexpectedly");
            _dialogs.Error(Loc.Get("Browsers_TheBackupSessionStoppedUnexpectedly") + ex.Message);
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
        BackupStatus = Loc.Get("Browsers_Cancelling");
        RestoreStatus = Loc.Get("Browsers_Cancelling");
    }

    [RelayCommand]
    private void BrowseArchive()
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.Get("Browsers_OpenACTRLZBrowser"),
            Filter = Loc.Get("Browsers_CTRLZBrowserBackupZip"),
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
        RestoreStatus = Loc.Get("Browsers_ReadingArchive");
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
                             Loc.Format("Browsers_ProfileS", info.Manifest.ProfileCount()) +
                             $"{FileUtil.FormatBytes(info.Manifest.TotalBytes)}";
            SelectedRestoreCount = RestoreProfiles.Count(p => p.IsSelected);
            ApplyArchiveTargets(info.Manifest);
            _services.State.Settings.LastRestoreArchive = ArchivePath;
            _services.State.SaveSettings();
            RestoreStatus = TargetBrowser is null
                ? Loc.Format("Browsers_ArchiveLoadedNoBrowserIs", info.Manifest.Family)
                : info.IsEncrypted
                    ? Loc.Get("Browsers_ArchiveUnlockedChooseProfilesAnd")
                    : Loc.Get("Browsers_ArchiveReadyChooseProfilesAnd");
            OnPropertyChanged(nameof(ArchiveContentsText));
            OnPropertyChanged(nameof(CanRestore));
        }
        catch (BrowserArchivePasswordException)
        {
            ArchiveSummary = string.Empty;
            RestoreStatus = Loc.Get("Browsers_ThisArchiveIsEncryptedEnter");
        }
        catch (Exception ex)
        {
            _services.Log.Write(LogLevel.Warning, "Archive inspection failed.", ex);
            ArchiveSummary = string.Empty;
            RestoreStatus = Loc.Get("Browsers_ArchiveCouldNotBeRead");
            _dialogs.Error(Loc.Get("Browsers_ThisArchiveCouldNotBe") + ex.Message);
        }
        finally
        {
            IsInspecting = false;
            UpdateRestoreCount();
        }
    }

    /// <summary>
    /// Reads the whole archive the way a restore would (every file decrypted and checksum-checked)
    /// without writing anything, so a damaged backup is found before the PC is wiped.
    /// </summary>
    [RelayCommand]
    private async Task VerifyArchiveAsync()
    {
        if (string.IsNullOrWhiteSpace(ArchivePath) || IsInspecting)
        {
            return;
        }

        IsInspecting = true;
        RestoreStatus = Loc.Get("Browsers_VerifyingTheBackupReadingEvery");
        var path = ArchivePath;
        var password = NullIfEmpty(ArchivePassword);
        try
        {
            var result = await Task.Run(() => new BrowserArchiveVerifier().Verify(path, password));
            RestoreStatus = result.IsRestorable
                ? Loc.Format("Browsers_VerifiedNothingWasChanged", result.Summary, FileUtil.FormatBytes(result.BytesChecked))
                : result.Summary;
            _services.Log.Write(result.IsRestorable ? LogLevel.Info : LogLevel.Warning,
                $"Backup verification: {(result.IsRestorable ? "restorable" : "not restorable")}, " +
                $"{result.FilesChecked} files, {result.Problems.Count} problems.");
            if (!result.IsRestorable)
            {
                _dialogs.Error(Loc.Get("Browsers_ThisBackupCannotBeFully") +
                               string.Join("\n", result.Problems.Take(6).Select(problem => "• " + problem)) +
                               (result.Problems.Count > 6 ? Loc.Format("Browsers_AndMore", result.Problems.Count - 6) : string.Empty) +
                               Loc.Get("Browsers_MakeANewBackupBefore"));
            }
        }
        catch (BrowserArchivePasswordException ex)
        {
            RestoreStatus = ex.Message;
        }
        catch (Exception ex)
        {
            _services.Log.Write(LogLevel.Warning, "Backup verification failed. " + LogPrivacy.Describe(ex));
            RestoreStatus = Loc.Get("Browsers_TheBackupCouldNotBe");
        }
        finally
        {
            IsInspecting = false;
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
            _dialogs.Info(Loc.Get("Browsers_SelectAtLeastOneProfile"));
            return;
        }

        if (!await EnsureBrowserClosedAsync(TargetBrowser))
        {
            return;
        }

        if (!RestoreAsNewProfiles &&
            !_dialogs.Confirm(Loc.Get("Browsers_OverwriteExistingProfiles"),
                Loc.Format("Browsers_TheSelectedProfilesWillBe", TargetBrowser.Name) +
                Loc.Get("Browsers_YourCurrentCookiesAndSaved") +
                Loc.Get("Browsers_RestoreAsNewProfilesInstead"),
                Loc.Get("Browsers_OverwriteProfiles"), Loc.Get("Browsers_Cancel2"), danger: true))
        {
            return;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsRestoring = true;
        // Keeps the updater from prompting or applying during a restore.
        using var activity = _services.Activity.Begin(Loc.Get("Browsers_RestoringBrowserProfiles"));
        RestoreProgress = 0;
        RestoreStatus = Loc.Get("Browsers_Restoring");
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
                var choice = _dialogs.Show(Loc.Get("Browsers_ProfilesAlreadyExist"),
                    Loc.Format("Browsers_TheseProfilesAlreadyExistIn", TargetBrowser.Name, string.Join(", ", ex.Profiles)) +
                    Loc.Get("Browsers_RestoreThemAsNewProfiles"),
                    Loc.Get("Browsers_RestoreAsNew"), Loc.Get("Browsers_Cancel2"));
                if (choice.Outcome != CtrlZ.App.Views.DialogOutcome.Primary)
                {
                    RestoreStatus = Loc.Get("Browsers_RestoreCancelled");
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
            RestoreStatus = Loc.Format("Browsers_RestoredInto", result.BrowserName);
            var warnings = result.Warnings.Count > 0
                ? Environment.NewLine + Environment.NewLine + "Notes:" + Environment.NewLine +
                  string.Join(Environment.NewLine, result.Warnings.Take(8))
                : string.Empty;
            var safety = string.IsNullOrWhiteSpace(result.SafetyBackupFolder)
                ? string.Empty
                : Loc.Format("Browsers_ASafetyCopyWasKept", result.SafetyBackupFolder);
            _dialogs.Info(
                Loc.Format("Browsers_RestoredProfileSInto", result.RestoredProfiles.Count, result.BrowserName) +
                Loc.Get("Browsers_StartTheBrowserAndCheck") +
                safety + warnings,
                Loc.Get("Browsers_BrowserRestoreComplete"));
            await DetectAsync();
        }
        catch (OperationCanceledException)
        {
            RestoreStatus = Loc.Get("Browsers_RestoreCancelled");
        }
        catch (BrowserArchivePasswordException)
        {
            RestoreStatus = Loc.Get("Browsers_TheArchivePasswordIsIncorrect");
            _dialogs.Error(Loc.Get("Browsers_ThatPasswordCouldNotUnlock"));
        }
        catch (BrowserRunningException ex)
        {
            RestoreStatus = Loc.Get("Browsers_CloseTheBrowserFirst");
            _dialogs.Error(ex.Message);
        }
        catch (Exception ex)
        {
            _services.Log.Write(LogLevel.Error, "Browser restore failed.", ex);
            RestoreStatus = Loc.Get("Browsers_RestoreFailed");
            _dialogs.Error(Loc.Get("Browsers_TheBrowserProfileCouldNot") + ex.Message);
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
        StatusMessage = Loc.Get("Browsers_DetectingFirefoxAndChromiumBrowsers");
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
                ? Loc.Get("Browsers_NoFirefoxOrChromiumBrowsers2")
                : Loc.Plural("Browsers_FoundCount", Browsers.Count);
            if (Browsers.Count == 0)
            {
                WarningText = Loc.Get("Browsers_FirefoxAndChromiumStoreProfiles");
            }

            OnPropertyChanged(nameof(HasBrowsers));
            OnPropertyChanged(nameof(HasNoBrowsers));
            OnPropertyChanged(nameof(DetectedSummary));
            TargetBrowser = RestoreTargets.FirstOrDefault();
            if (ArchiveInfo is { } archive)
            {
                ApplyArchiveTargets(archive.Manifest);
            }

            RecalculateEstimate();
        }
        catch (Exception ex)
        {
            _services.Log.Write(LogLevel.Error, "Browser detection failed.", ex);
            StatusMessage = Loc.Get("Browsers_BrowserDetectionFailed");
            WarningText = ex.Message;
            _dialogs.Error(Loc.Get("Browsers_CTRLZCouldNotDetect") + ex.Message);
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
                browser.EstimatedText = Loc.Get("Browsers_SizeUnavailable");
            }
        }

        foreach (var browser in Browsers.Where(b => b.SelectedProfiles.Count == 0))
        {
            browser.EstimatedText = string.Empty;
        }

        EstimateText = SelectedProfileCount == 0
            ? Loc.Get("Browsers_SelectABrowserAndAt")
            : Loc.Plural("Browsers_ProfilesEstimated", SelectedProfileCount, FileUtil.FormatBytes(totalBytes));
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
            Loc.Get("Browsers_CloseTheBrowser"),
            Loc.Format("Browsers_IsRunningABackupNeeds", browser.Name) +
            Loc.Get("Browsers_CTRLZCanCloseIt"),
            Loc.Get("Browsers_CloseBrowser"),
            Loc.Get("Browsers_Skip"));
        if (choice.Outcome != CtrlZ.App.Views.DialogOutcome.Primary)
        {
            return false;
        }

        if (await _services.BrowserProcesses.CloseAsync(browser.Browser, force: false))
        {
            return true;
        }

        var force = _dialogs.Show(
            Loc.Get("Browsers_ForceClose"),
            Loc.Format("Browsers_DidNotCloseGracefullyForce", browser.Name),
            Loc.Get("Browsers_ForceClose2"),
            Loc.Get("Browsers_Skip"),
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
