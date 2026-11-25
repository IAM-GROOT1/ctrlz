// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Windows;

namespace CtrlZ.App.Views;

public enum WelcomeChoice
{
    /// <summary>Closed without choosing; stay where the app is.</summary>
    Dismissed,

    /// <summary>Preparing to reinstall: start on the Scan page.</summary>
    Scan,

    /// <summary>Already on the new install: go to My Apps to import a profile.</summary>
    FreshInstall
}

/// <summary>The first-run guide. Shown once for a new user, and again from Settings on request.</summary>
public partial class WelcomeWindow : Window
{
    public WelcomeWindow()
    {
        InitializeComponent();
        Services.TextScale.Apply(this);
        Loaded += (_, _) => StartButton.Focus();
    }

    public WelcomeChoice Choice { get; private set; } = WelcomeChoice.Dismissed;

    private void Scan_Click(object sender, RoutedEventArgs e) => Finish(WelcomeChoice.Scan);

    private void FreshInstall_Click(object sender, RoutedEventArgs e) => Finish(WelcomeChoice.FreshInstall);

    private void Close_Click(object sender, RoutedEventArgs e) => Finish(WelcomeChoice.Dismissed);

    private void Finish(WelcomeChoice choice)
    {
        Choice = choice;
        Close();
    }
}
