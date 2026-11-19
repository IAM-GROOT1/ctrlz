// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CtrlZ.App.Services;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Safety;

namespace CtrlZ.App.ViewModels;

/// <summary>
/// The "Ready to wipe?" page: a read-only pass/warning/fail report with a re-scan button,
/// per-item handled/ignored state saved locally, and a link to drift detection.
/// </summary>
public sealed partial class ReadinessViewModel : PageViewModelBase
{
    private const string NoScanSummary =
        "A check sizes your folders and looks for material CTRL+Z cannot back up. It never changes a file.";

    private readonly AppServices _services;
    private readonly DialogService _dialogs;
    private readonly MainViewModel _main;
    private readonly SafetyStateStore _stateStore;
    private readonly ReadinessScanner _scanner = new();

    private CancellationTokenSource? _cts;

    public ReadinessViewModel(AppServices services, DialogService dialogs, MainViewModel main)
        : base("Ready to wipe?", "What a clean install would remove. Runs locally: no network, no telemetry.")
    {
        _services = services;
        _dialogs = dialogs;
        _main = main;
        _stateStore = new SafetyStateStore(
            Path.Combine(services.Paths.State, "safety-state.json"),
            services.Log);

        RowsView = CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = FilterRow;
    }

    public ObservableCollection<ReadinessRow> Rows { get; } = new();

    public ICollectionView RowsView { get; }

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _statusLine = "Run a check to see what would be lost.";

    [ObservableProperty]
    private string _summaryTitle = "Not checked yet";

    [ObservableProperty]
    private string _summaryDetail = NoScanSummary;

    [ObservableProperty]
    private ReadinessStatus _overallStatus = ReadinessStatus.Pass;

    [ObservableProperty]
    private bool _showHandled;

    [ObservableProperty]
    private int _passCount;

    [ObservableProperty]
    private int _warningCount;

    [ObservableProperty]
    private int _failCount;

    [ObservableProperty]
    private int _resolvedCount;

    public bool HasRows => Rows.Count > 0;

    public bool HasVisibleRows => !RowsView.IsEmpty;

    public bool CanCancel => IsScanning;

    public string CountsText =>
        $"{PassCount} fine · {WarningCount} to check · {FailCount} needing action" +
        (ResolvedCount > 0 ? $" · {ResolvedCount} handled or ignored" : string.Empty);

    public override async Task ActivateAsync()
    {
        if (Rows.Count == 0 && !IsScanning)
        {
            await ScanAsync();
        }
    }

    partial void OnIsScanningChanged(bool value) => OnPropertyChanged(nameof(CanCancel));

    partial void OnShowHandledChanged(bool value)
    {
        RowsView.Refresh();
        OnPropertyChanged(nameof(HasVisibleRows));
    }

    partial void OnPassCountChanged(int value) => NotifyCounts();

    partial void OnWarningCountChanged(int value) => NotifyCounts();

    partial void OnFailCountChanged(int value) => NotifyCounts();

    partial void OnResolvedCountChanged(int value) => NotifyCounts();

    private void NotifyCounts() => OnPropertyChanged(nameof(CountsText));

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning)
        {
            return;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsScanning = true;
        StatusLine = "Scanning…";
        Rows.Clear();

        try
        {
            var request = await BuildRequestAsync(_cts.Token);
            var progress = new Progress<ReadinessProgress>(update =>
                StatusLine = $"Scanning {update.Step} ({update.Index}/{update.Total})…");

            var report = await _scanner.ScanAsync(request, progress, _cts.Token);
            ApplyReport(report);
            StatusLine = "Check complete.";
        }
        catch (OperationCanceledException)
        {
            StatusLine = "Check cancelled.";
            SummaryTitle = Rows.Count == 0 ? "Not checked yet" : SummaryTitle;
        }
        catch (Exception ex)
        {
            _services.Log.Write(LogLevel.Error, "The readiness check failed.", ex);
            StatusLine = "The check could not finish.";
            _dialogs.Error("The check could not finish.\n\n" + ex.Message);
        }
        finally
        {
            IsScanning = false;
            OnPropertyChanged(nameof(HasRows));
            OnPropertyChanged(nameof(HasVisibleRows));
        }
    }

    [RelayCommand]
    private void CancelScan() => _cts?.Cancel();

    [RelayCommand]
    private void ToggleHandled(ReadinessRow? row)
    {
        if (row is null)
        {
            return;
        }

        row.IsHandled = !row.IsHandled;
        if (row.IsHandled)
        {
            row.IsIgnored = false;
        }

        Persist(row);
    }

    [RelayCommand]
    private void ToggleIgnored(ReadinessRow? row)
    {
        if (row is null)
        {
            return;
        }

        row.IsIgnored = !row.IsIgnored;
        if (row.IsIgnored)
        {
            row.IsHandled = false;
        }

        Persist(row);
    }

    [RelayCommand]
    private void ToggleDetails(ReadinessRow? row)
    {
        if (row is not null)
        {
            row.IsExpanded = !row.IsExpanded;
        }
    }

    [RelayCommand]
    private void ToggleShowHandled() => ShowHandled = !ShowHandled;

    [RelayCommand]
    private void OpenDrift() => _dialogs.ShowDrift(_services, _main);

    private void Persist(ReadinessRow row)
    {
        var state = _stateStore.Load();

        if (row.IsHandled)
        {
            SafetyStateStore.Add(state.HandledChecks, row.Id);
        }
        else
        {
            SafetyStateStore.Remove(state.HandledChecks, row.Id);
        }

        if (row.IsIgnored)
        {
            SafetyStateStore.Add(state.IgnoredChecks, row.Id);
        }
        else
        {
            SafetyStateStore.Remove(state.IgnoredChecks, row.Id);
        }

        _stateStore.Save(state);
        RefreshSummary();
    }

    private void ApplyReport(ReadinessReport report)
    {
        var state = _stateStore.Load();
        Rows.Clear();

        foreach (var check in report.Checks)
        {
            Rows.Add(new ReadinessRow(
                check,
                SafetyStateStore.Contains(state.HandledChecks, check.Id),
                SafetyStateStore.Contains(state.IgnoredChecks, check.Id)));
        }

        RefreshSummary();

        // Counts only: no user folder names or file names reach the log.
        _services.Log.Write(
            LogLevel.Info,
            $"Readiness check: {report.PassCount} pass, {report.WarningCount} warning, {report.FailCount} fail.");
    }

    private void RefreshSummary()
    {
        var open = Rows.Where(row => !row.IsResolved).ToList();
        PassCount = open.Count(row => row.Status == ReadinessStatus.Pass);
        WarningCount = open.Count(row => row.Status == ReadinessStatus.Warning);
        FailCount = open.Count(row => row.Status == ReadinessStatus.Fail);
        ResolvedCount = Rows.Count - open.Count;

        OverallStatus = FailCount > 0 ? ReadinessStatus.Fail
            : WarningCount > 0 ? ReadinessStatus.Warning
            : ReadinessStatus.Pass;

        if (Rows.Count == 0)
        {
            SummaryTitle = "Not checked yet";
            SummaryDetail = NoScanSummary;
        }
        else if (open.Count == 0)
        {
            SummaryTitle = "Ready to wipe";
            SummaryDetail = "Everything on the list is handled or ignored. Copy anything you still need to external media first.";
        }
        else
        {
            // Only warnings and failures need action; a passing row should not be counted as
            // something to look at, even though it still appears in the list.
            var needing = WarningCount + FailCount;
            var noun = needing == 1 ? "item needs" : "items need";

            SummaryTitle = needing == 0
                ? $"Ready to wipe — all {open.Count} items look fine"
                : $"{needing} {noun} your attention";
            SummaryDetail = "Items you mark handled or ignore drop off this list and are remembered on this PC.";
        }

        RowsView.Refresh();
        OnPropertyChanged(nameof(HasVisibleRows));
        OnPropertyChanged(nameof(HasRows));
    }

    private bool FilterRow(object item) =>
        ShowHandled || item is not ReadinessRow row || !row.IsResolved;

    private async Task<ReadinessRequest> BuildRequestAsync(CancellationToken cancellationToken)
    {
        var environment = SafetyEnvironment.Detect();
        var settings = _services.State.Settings;
        var profile = _services.State.ActiveProfile;

        return new ReadinessRequest
        {
            Environment = environment,
            BackupFolder = settings.LastBackupFolder,
            BackupWarningDays = settings.BackupWarningDays,
            UserFolders = BuildUserFolders(environment),
            Browsers = await DetectBrowsersAsync(cancellationToken),
            ProfileUpdatedUtc = profile.UpdatedUtc,
            ProfileAppCount = profile.Apps.Count
        };
    }

    private static List<UserFolder> BuildUserFolders(SafetyEnvironment environment)
    {
        var folders = new List<UserFolder>();

        void Add(string label, string path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                folders.Add(new UserFolder(label, path));
            }
        }

        Add("Desktop", SafeFolder(Environment.SpecialFolder.Desktop));
        Add("Documents", SafeFolder(Environment.SpecialFolder.MyDocuments));
        Add("Downloads", Path.Combine(environment.HomeDirectory, "Downloads"));
        Add("Pictures", SafeFolder(Environment.SpecialFolder.MyPictures));
        Add("Videos", SafeFolder(Environment.SpecialFolder.MyVideos));
        Add("Music", SafeFolder(Environment.SpecialFolder.MyMusic));

        // Other fixed drives get a deliberately shallow look: listing a whole drive in full
        // is slow and the point is only to say "there is a lot here, and it is not backed up".
        var quickLook = new FolderUsageOptions
        {
            MaxDepth = 2,
            MaxFiles = 5_000,
            MaxDuration = TimeSpan.FromSeconds(5)
        };

        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? string.Empty;
        foreach (var drive in environment.FixedDrives)
        {
            var root = Path.GetPathRoot(drive) ?? drive;
            if (string.Equals(root, systemDrive, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            folders.Add(new UserFolder($"Drive {root.TrimEnd('\\')}", root, quickLook));
        }

        return folders;
    }

    private static string SafeFolder(Environment.SpecialFolder folder)
    {
        try
        {
            return Environment.GetFolderPath(folder);
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task<IReadOnlyList<DetectedBrowserSummary>> DetectBrowsersAsync(CancellationToken cancellationToken)
    {
        try
        {
            var detected = await _services.BrowserDetector.DetectAsync(
                _services.State.Settings.CustomBrowserUserDataPath,
                cancellationToken);
            return detected
                .Select(browser => new DetectedBrowserSummary(browser.DisplayName, browser.Profiles.Count))
                .ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _services.Log.Write(LogLevel.Warning, "Browser detection failed during the readiness check.", ex);
            return [];
        }
    }
}
