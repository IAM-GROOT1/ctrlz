// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Windows;
using CtrlZ.App.ViewModels;

namespace CtrlZ.App.Views;

public partial class CommandReviewWindow : Window
{
    public CommandReviewWindow()
    {
        InitializeComponent();
        Services.TextScale.Apply(this);

        Loaded += (_, _) =>
        {
            if (DataContext is CommandReviewViewModel viewModel)
            {
                viewModel.RequestClose += (_, _) => Close();
            }
        };
    }
}
