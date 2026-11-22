// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Windows;
using CtrlZ.App.ViewModels;
using CtrlZ.App.Views;
using CtrlZ.Core.Install;

namespace CtrlZ.App.Services;

public sealed class DialogService
{
    public bool Confirm(
        string title,
        string message,
        string primary = "Continue",
        string secondary = "Cancel",
        bool danger = false)
    {
        var result = Show(title, message, primary, secondary, null, null, null, danger);
        return result.Outcome == DialogOutcome.Primary;
    }

    public bool? ConfirmWithCancel(
        string title,
        string message,
        string primary,
        string secondary,
        bool danger = false)
    {
        var result = Show(title, message, primary, secondary, null, null, null, danger);
        return result.Outcome switch
        {
            DialogOutcome.Primary => true,
            DialogOutcome.Secondary => false,
            _ => null
        };
    }

    public DialogResult Show(
        string title,
        string message,
        string primary = "OK",
        string? secondary = "Cancel",
        string? inputLabel = null,
        string? initialInput = null,
        string? checkText = null,
        bool danger = false)
    {
        var window = new DialogWindow();
        window.Configure(title, message, primary, secondary, inputLabel, initialInput, checkText, danger);
        if (Application.Current.MainWindow is { IsVisible: true } owner && owner != window)
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        window.ShowDialog();
        return window.Result;
    }

    public string? Prompt(string title, string message, string label, string? initial = null)
    {
        var result = Show(title, message, "Save", "Cancel", label, initial);
        return result.Outcome == DialogOutcome.Primary ? result.InputText : null;
    }

    public void Error(string message, string title = "Something went wrong")
    {
        Show(title, message, "Close", null, danger: true);
    }

    public void Info(string message, string title = "CTRL+Z")
    {
        Show(title, message, "Close", null);
    }

    /// <summary>Opens the profile drift window and returns when it closes.</summary>
    public void ShowDrift(AppServices services, MainViewModel main)
    {
        var window = new DriftWindow { DataContext = new DriftViewModel(services, this, main) };

        if (Application.Current.MainWindow is { IsVisible: true } owner && owner != window)
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        window.ShowDialog();
    }

    /// <summary>
    /// Shows the install preview and returns true when the user chose to continue. Used as the
    /// final confirmation before a run, and controllable from Settings.
    /// </summary>
    public bool ShowInstallPreview(AppServices services, CtrlZ.Core.Models.AppProfile profile)
    {
        var viewModel = new PreviewViewModel(services, this, profile, profile.Name);
        var window = new PreviewWindow { DataContext = viewModel };

        if (Application.Current.MainWindow is { IsVisible: true } owner && owner != window)
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        window.ShowDialog();
        return viewModel.ProceedRequested;
    }

    /// <summary>Opens the command review screen for the active profile. True when anything changed.</summary>
    public bool ShowCommandReview(AppServices services, CtrlZ.Core.Models.AppProfile profile)
    {
        var approvals = new PostInstallApprovalStore(
            System.IO.Path.Combine(services.Paths.State, PostInstallStep.ApprovalsFileName));
        var viewModel = new CommandReviewViewModel(
            profile,
            services.State.OriginOf(profile),
            approvals,
            (title, message) => Confirm(title, message, "Yes, I understand", "Cancel", danger: true),
            () => services.State.MarkActiveAsLocal(userConfirmed: true));

        var window = new CommandReviewWindow { DataContext = viewModel };
        if (Application.Current.MainWindow is { IsVisible: true } owner && owner != window)
        {
            window.Owner = owner;
        }

        window.ShowDialog();
        return viewModel.Changed;
    }

    /// <summary>
    /// Asks for the confirmations the commands in this run need: once per command text on this PC,
    /// and again on every run for a risky command. False means the install must not start.
    /// </summary>
    public bool ConfirmPostInstallCommands(AppServices services, CtrlZ.Core.Models.AppProfile profile)
    {
        var approvals = new PostInstallApprovalStore(
            System.IO.Path.Combine(services.Paths.State, PostInstallStep.ApprovalsFileName));
        var firstRun = new PostInstallFirstRunStore(
            System.IO.Path.Combine(services.Paths.State, PostInstallFirstRunStore.FileName));
        var lines = CommandPreview.ForProfile(profile, services.State.OriginOf(profile), approvals);
        var consent = PostInstallConsent.Required(lines, firstRun.ConfirmedFor(profile.Id));

        if (consent.NeedsFirstRunConfirmation)
        {
            var list = string.Join("\n", consent.WillRun.Select(line => $"• {line.AppName}: {line.CommandText}"));
            var count = consent.WillRun.Count;
            if (!Confirm(
                    "Run post-install commands?",
                    $"This profile will run {count} command{(count == 1 ? string.Empty : "s")} on this PC after their apps install:\n\n{list}",
                    "Run them", "Cancel install"))
            {
                return false;
            }

            firstRun.Record(profile.Id, consent.WillRun.Select(line => line.Hash));
        }

        foreach (var risky in consent.Risky)
        {
            if (!Confirm(
                    "Run a risky command?",
                    $"{risky.AppName} will run a command that matches a risky pattern:\n\n{risky.CommandText}\n\n" +
                    "This is asked on every run.",
                    "Run it", "Cancel install", danger: true))
            {
                return false;
            }
        }

        return true;
    }
}
