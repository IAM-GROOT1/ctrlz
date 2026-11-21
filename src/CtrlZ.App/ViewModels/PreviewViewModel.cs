// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CtrlZ.App.Services;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Models;
using CtrlZ.Core.Safety;

namespace CtrlZ.App.ViewModels;

/// <summary>One app in the install preview, with its optional dry-run result.</summary>
public sealed partial class PreviewRow : ObservableObject
{
    public PreviewRow(InstallPlanItem item)
    {
        Item = item;
        WarningText = string.Join(" ", item.Warnings);
    }

    public InstallPlanItem Item { get; }
    public string Name => Item.Name;
    public string OrderText => $"{Item.Order}.";
    public string SourceText => Item.SourceDescription;
    public string SilentText => Item.SilentArgs;
    public string AdminText => Item.RequiresAdmin ? "May need admin" : "No admin needed";
    public string WarningText { get; }
    public bool HasWarnings => Item.Warnings.Count > 0;
    public string NotesText => string.Join(" ", Item.Notes);

    [ObservableProperty]
    private string _dryRunText = string.Empty;

    [ObservableProperty]
    private PlanCheckStatus? _dryRunStatus;

    public bool HasDryRun => DryRunStatus is not null || DryRunText.Length > 0;
}

/// <summary>
/// The install preview: what will happen, in what order, with which switches, plus a dry run
/// that verifies every source without installing anything.
/// </summary>
public sealed partial class PreviewViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly DialogService _dialogs;
    private readonly InstallPlan _plan;

    public PreviewViewModel(AppServices services, DialogService dialogs, AppProfile profile, string profileName)
    {
        _services = services;
        _dialogs = dialogs;
        _plan = new InstallPlanner().Build(profile, profileName);

        foreach (var item in _plan.Items)
        {
            Items.Add(new PreviewRow(item));
        }

        SummaryText = _plan.IsEmpty
            ? "There is nothing enabled to install."
            : $"{_plan.Count} app{(_plan.Count == 1 ? string.Empty : "s")} in this run · " +
              $"{_plan.AdminCount} may need admin · {_plan.WarningCount} with warnings";

        PlanWarnings = _plan.PlanWarnings;
    }

    public ObservableCollection<PreviewRow> Items { get; } = new();

    public IReadOnlyList<string> PlanWarnings { get; }

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private string _statusLine = "Review the list, then run the dry run or start the install.";

    [ObservableProperty]
    private string _dryRunSummary = string.Empty;

    [ObservableProperty]
    private bool _isDryRunning;

    /// <summary>True when the user chose to start the install.</summary>
    public bool ProceedRequested { get; private set; }

    private DryRunReport? _lastReport;

    public event EventHandler? RequestClose;

    public bool HasBlockingFailures => _lastReport?.HasFailures == true;

    [RelayCommand]
    private async Task DryRunAsync()
    {
        if (IsDryRunning)
        {
            return;
        }

        IsDryRunning = true;
        DryRunSummary = string.Empty;
        StatusLine = "Checking each source…";

        try
        {
            var runner = new DryRunRunner(
                (id, token) => _services.Winget.SearchAsync(id, token),
                (repo, token) => _services.GitHub.GetLatestAsync(repo, token),
                (url, token) => _services.Downloads.HeadAsync(url, TimeSpan.FromSeconds(10), token));

            var byId = _services.State.ActiveProfile.Apps.ToDictionary(app => app.Id);
            var progress = new Progress<string>(message => StatusLine = message);

            _lastReport = await runner.RunAsync(_plan, byId, progress, CancellationToken.None);

            foreach (var row in Items)
            {
                var result = _lastReport.Items.FirstOrDefault(item => item.AppId == row.Item.AppId);
                if (result is null)
                {
                    continue;
                }

                row.DryRunStatus = result.Status;
                row.DryRunText = result.Message;
            }

            DryRunSummary =
                $"{_lastReport.ReadyCount} ready · {_lastReport.WarningCount} warnings · {_lastReport.FailedCount} failed" +
                (_lastReport.KnownBytes > 0
                    ? $" · {FileUtil.FormatBytes(_lastReport.KnownBytes)} known download"
                    : string.Empty);

            StatusLine = _lastReport.HasFailures
                ? "Some apps cannot be installed as configured. Nothing has been installed."
                : "Dry run complete. Nothing has been installed.";

            OnPropertyChanged(nameof(HasBlockingFailures));
        }
        catch (Exception ex)
        {
            _services.Log.Write(LogLevel.Error, "The install dry run failed. " + LogPrivacy.Describe(ex));
            StatusLine = "The dry run could not finish.";
            _dialogs.Error("The dry run could not finish.\n\n" + ex.Message);
        }
        finally
        {
            IsDryRunning = false;
        }
    }

    [RelayCommand]
    private void ExportReport()
    {
        if (_lastReport is null)
        {
            _dialogs.Info("Run the dry run first, then export its report.", "Nothing to export");
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save dry-run report",
            Filter = "Text file (*.txt)|*.txt",
            FileName = $"ctrlz-dry-run-{DateTime.Now:yyyyMMdd-HHmm}.txt"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, _lastReport.ToText());
            StatusLine = "Report saved.";
        }
        catch (Exception ex)
        {
            _services.Log.Write(LogLevel.Error, "Could not save the dry-run report. " + LogPrivacy.Describe(ex));
            _dialogs.Error("Could not save the report.\n\n" + ex.Message);
        }
    }

    [RelayCommand]
    private void Proceed()
    {
        ProceedRequested = true;
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(this, EventArgs.Empty);
}
