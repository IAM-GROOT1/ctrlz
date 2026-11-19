// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Windows;
using System.Windows.Controls;
using CtrlZ.App.ViewModels;

namespace CtrlZ.App.Views;

public partial class ReadinessView : UserControl
{
    public ReadinessView()
    {
        InitializeComponent();
    }

    private ReadinessViewModel? ViewModel => DataContext as ReadinessViewModel;

    private void Handled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ReadinessRow row } && ViewModel is { } viewModel)
        {
            viewModel.ToggleHandledCommand.Execute(row);
        }
    }

    private void Ignore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ReadinessRow row } && ViewModel is { } viewModel)
        {
            viewModel.ToggleIgnoredCommand.Execute(row);
        }
    }

    private void Drift_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.OpenDriftCommand.Execute(null);
        }
    }
}
