// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Windows;
using System.Windows.Controls;
using CtrlZ.App.ViewModels;

namespace CtrlZ.App.Views;

public partial class BrowsersView : UserControl
{
    public BrowsersView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => SyncPasswords();
    }

    private void BackupPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is BrowsersViewModel viewModel)
        {
            viewModel.BackupPassword = BackupPasswordBox.Password;
        }
    }

    private void ArchivePasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is BrowsersViewModel viewModel)
        {
            viewModel.ArchivePassword = ArchivePasswordBox.Password;
        }
    }

    private void SyncPasswords()
    {
        if (DataContext is BrowsersViewModel viewModel)
        {
            BackupPasswordBox.Password = viewModel.BackupPassword ?? string.Empty;
            ArchivePasswordBox.Password = viewModel.ArchivePassword ?? string.Empty;
        }
    }
}
