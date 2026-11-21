// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Windows;
using CtrlZ.App.ViewModels;

namespace CtrlZ.App.Views;

public partial class PreviewWindow : Window
{
    public PreviewWindow()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            if (DataContext is PreviewViewModel viewModel)
            {
                viewModel.RequestClose += (_, _) => Close();
            }
        };
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
