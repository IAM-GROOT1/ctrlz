// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Windows;
using CtrlZ.App.ViewModels;
using CtrlZ.App.Views;

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
}
