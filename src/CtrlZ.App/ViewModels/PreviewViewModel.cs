// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.App.Localization;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CtrlZ.App.Services;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Install;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Models;
using CtrlZ.Core.Safety;

namespace CtrlZ.App.ViewModels;

/// <summary>One app in the install preview, with its optional dry-run result.</summary>
public sealed partial class PreviewRow : ObservableObject
{
    public PreviewRow(InstallPlanItem item, CommandPreviewLine? command = null)
    {
        Item = item;
        Command = command;
        WarningText = string.Join(" ", item.Warnings);
    }

    /// <summary>The post-install command for this app, shown in full, or null.</summary>
    public CommandPreviewLine? Command { get; }
    public bool HasCommand => Command is not null;
    public string CommandText => CtrlZ.Core.Cli.CliText.Safe(Command?.CommandText);
    public string CommandStateText => Command?.StateText ?? string.Empty;

    /// <summary>True when the command will not run or matches a risky pattern.</summary>
    public bool CommandNeedsAttention => Command is { } line && (!line.WillRun || line.Risky);

    public InstallPlanItem Item { get; }
    public string Name => Item.Name;

    /// <summary>What screen readers announce for this row.</summary>
    public override string ToString() => Name;

    public string OrderText => $"{Item.Order}.";
    public string SourceText => Item.SourceDescription;
    public string SilentText => Item.SilentArgs;
    public string AdminText => Item.RequiresAdmin ? Loc.Get("Preview_MayNeedAdmin") : Loc.Get("Preview_NoAdminNeeded");
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
    private readonly AppProfile _profile;

    public PreviewViewModel(AppServices services, DialogService dialogs, AppProfile profile, string profileName)
    {
        _services = services;
        _dialogs = dialogs;
        _profile = profile;

        // Same catalog and order as the install engine, so the preview shows the real run.
        _plan = new InstallPlanner(catalog: services.InstallEngine.Catalog).Build(profile, profileName);
        LoadCommands();

        UpdateSummary();

        PlanWarnings = _plan.PlanWarnings;
    }

    public ObservableCollection<PreviewRow> Items { get; } = new();

    /// <summary>Every post-install command in this run, in install order.</summary>
    public IReadOnlyList<CommandPreviewLine> Commands { get; private set; } = [];

    /// <summary>
    /// The line above the list. Commands that are blocked until reviewed, or that match a risky
    /// pattern, are counted here too, so the summary never reads as all clear while they wait.
    /// </summary>
    private void UpdateSummary() => SummaryText = Summary(_plan, Commands);

    public static string Summary(InstallPlan plan, IReadOnlyList<CommandPreviewLine> commands)
    {
        if (plan.IsEmpty)
        {
            return Loc.Get("Preview_ThereIsNothingEnabledTo");
        }

        var toReview = commands.Count(line => !CommandStateResolver.MayRun(line.State));
        var risky = commands.Count(line => line.Risky);
        return Loc.Plural("Preview_AppsInRun", plan.Count) +
               Loc.Format("Preview_MayNeedAdminWithWarnings", plan.AdminCount, plan.WarningCount) +
               (toReview > 0 ? Loc.Plural("Preview_CommandsToReview", toReview) : string.Empty) +
               (risky > 0 ? Loc.Plural("Preview_RiskyCommands", risky) : string.Empty);
    }

    /// <summary>True when some command is blocked until it is reviewed.</summary>
    public bool HasCommandsToReview => Commands.Any(line => !CommandStateResolver.MayRun(line.State));

    /// <summary>Reads approvals and origin fresh and rebuilds the rows. Never writes anything.</summary>
    private void LoadCommands()
    {
        var approvals = new PostInstallApprovalStore(Path.Combine(_services.Paths.State, PostInstallStep.ApprovalsFileName));
        Commands = CommandPreview.ForPlan(
            _plan, _profile, _services.State.OriginOf(_profile), approvals);
        var commandByApp = Commands
            .GroupBy(line => line.AppId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        Items.Clear();
        foreach (var item in _plan.Items)
        {
            var row = new PreviewRow(item, commandByApp.GetValueOrDefault(item.AppId));
            if (_lastReport?.Items.FirstOrDefault(result => result.AppId == item.AppId) is { } result)
            {
                row.DryRunStatus = result.Status;
                row.DryRunText = result.Message;
            }

            Items.Add(row);
        }

        if (_lastReport is not null)
        {
            _lastReport = _lastReport with { Commands = Commands };
        }

        OnPropertyChanged(nameof(Commands));
        OnPropertyChanged(nameof(HasCommandsToReview));
    }

    [RelayCommand]
    private void ReviewCommands()
    {
        if (_dialogs.ShowCommandReview(_services, _profile))
        {
            LoadCommands();
            UpdateSummary();
            StatusLine = Loc.Get("Preview_CommandReviewSavedOnThis");
        }
    }

    public IReadOnlyList<string> PlanWarnings { get; }

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private string _statusLine = Loc.Get("Preview_ReviewTheListThenRun");

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
        StatusLine = Loc.Get("Preview_CheckingEachSource");

        try
        {
            var runner = new DryRunRunner(
                (id, token) => _services.Winget.SearchAsync(id, token),
                (repo, token) => _services.GitHub.GetLatestAsync(repo, token),
                (url, token) => _services.Downloads.HeadAsync(url, TimeSpan.FromSeconds(10), token));

            var byId = _services.State.ActiveProfile.Apps.ToDictionary(app => app.Id);
            var progress = new Progress<string>(message => StatusLine = message);

            _lastReport = (await runner.RunAsync(_plan, byId, progress, CancellationToken.None)) with { Commands = Commands };

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
                Loc.Format("Preview_ReadyWarningsFailed", _lastReport.ReadyCount, _lastReport.WarningCount, _lastReport.FailedCount) +
                (_lastReport.KnownBytes > 0
                    ? Loc.Format("Preview_KnownDownload", FileUtil.FormatBytes(_lastReport.KnownBytes))
                    : string.Empty);

            StatusLine = _lastReport.HasFailures
                ? Loc.Get("Preview_SomeAppsCannotBeInstalled")
                : Loc.Get("Preview_DryRunCompleteNothingHas");

            OnPropertyChanged(nameof(HasBlockingFailures));
        }
        catch (Exception ex)
        {
            _services.Log.Write(LogLevel.Error, "The install dry run failed. " + LogPrivacy.Describe(ex));
            StatusLine = Loc.Get("Preview_TheDryRunCouldNot");
            _dialogs.Error(Loc.Get("Preview_TheDryRunCouldNot2") + ex.Message);
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
            _dialogs.Info(Loc.Get("Preview_RunTheDryRunFirst"), Loc.Get("Preview_NothingToExport"));
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.Get("Preview_SaveReportTitle"),
            Filter = Loc.Get("Preview_ReportFilter"),
            FileName = $"ctrlz-dry-run-{DateTime.Now:yyyyMMdd-HHmm}.txt"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var export = _lastReport.WithExportNotes(_profile.Apps, _services.State.Settings);
            File.WriteAllText(dialog.FileName, export.ToText());
            StatusLine = Loc.Get("Preview_ReportSaved");
        }
        catch (Exception ex)
        {
            _services.Log.Write(LogLevel.Error, "Could not save the dry-run report. " + LogPrivacy.Describe(ex));
            _dialogs.Error(Loc.Get("Preview_CouldNotSaveTheReport") + ex.Message);
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
