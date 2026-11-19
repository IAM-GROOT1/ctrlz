// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Windows;
using CtrlZ.App.ViewModels;

namespace CtrlZ.App.Views;

public partial class DriftWindow : Window
{
    public DriftWindow()
    {
        InitializeComponent();

        Loaded += async (_, _) =>
        {
            if (DataContext is DriftViewModel viewModel)
            {
                viewModel.RequestClose += (_, _) => Close();
                await viewModel.LoadAsync();
            }
        };
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
