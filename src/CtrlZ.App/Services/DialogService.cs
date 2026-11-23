// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.App.Localization;
using System.Windows;
using CtrlZ.App.ViewModels;
using CtrlZ.App.Views;
using CtrlZ.Core.Install;

namespace CtrlZ.App.Services;

public sealed class DialogService
{
    /// <summary>Marks "use the translated Cancel" for <see cref="Show"/>; null there means no second button.</summary>
    private const string DefaultCancel = "\u0001cancel";

    /// <param name="primary">Defaults to "Continue" in the current language.</param>
    /// <param name="secondary">Defaults to "Cancel" in the current language.</param>
    public bool Confirm(
        string title,
        string message,
        string? primary = null,
        string? secondary = null,
        bool danger = false)
    {
        var result = Show(
            title,
            message,
            primary ?? Loc.Get("DialogService_Continue"),
            secondary ?? Loc.Get("DialogService_Cancel"),
            null,
            null,
            null,
            danger);
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
        string? primary = null,
        string? secondary = DefaultCancel,
        string? inputLabel = null,
        string? initialInput = null,
        string? checkText = null,
        bool danger = false)
    {
        var window = new DialogWindow();
        window.Configure(
            title,
            message,
            primary ?? Loc.Get("DialogService_Ok"),
            string.Equals(secondary, DefaultCancel, StringComparison.Ordinal) ? Loc.Get("DialogService_Cancel") : secondary,
            inputLabel,
            initialInput,
            checkText,
            danger);
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
        var result = Show(title, message, Loc.Get("DialogService_Save"), Loc.Get("DialogService_Cancel"), label, initial);
        return result.Outcome == DialogOutcome.Primary ? result.InputText : null;
    }

    public void Error(string message, string? title = null)
    {
        Show(title ?? Loc.Get("DialogService_SomethingWentWrong"), message, Loc.Get("DialogService_Close"), null, danger: true);
    }

    public void Info(string message, string title = "CTRL+Z")
    {
        Show(title, message, Loc.Get("DialogService_Close"), null);
    }

    /// <summary>Opens the profile drift window and returns when it closes.</summary>
    /// <summary>Shows the first-run guide and returns where the user wants to start.</summary>
    public WelcomeChoice ShowWelcome()
    {
        var window = new WelcomeWindow();
        if (Application.Current.MainWindow is { IsVisible: true } owner && owner != window)
        {
            window.Owner = owner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        window.ShowDialog();
        return window.Choice;
    }

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
            (title, message) => Confirm(title, message, Loc.Get("DialogService_YesIUnderstand"), Loc.Get("DialogService_Cancel"), danger: true),
            () => services.State.MarkAsLocal(profile, userConfirmed: true));

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
    public bool ConfirmPostInstallCommands(AppServices services, CtrlZ.Core.Models.AppProfile profile) =>
        InstallStartGuard.CommandConsent(
            services.Paths.State,
            services.State.OriginOf,
            (title, message, danger) => Confirm(title, message, danger ? Loc.Get("DialogService_RunIt") : Loc.Get("DialogService_RunThem"), Loc.Get("DialogService_CancelInstall"), danger))(profile);
}
