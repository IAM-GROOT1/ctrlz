// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.App.Localization;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using CtrlZ.Core.Models;
using CtrlZ.Core.Services;

namespace CtrlZ.App.Views;

public partial class WingetMatchWindow : Window
{
    private readonly WingetCli _winget;
    private readonly string _initialQuery;

    public WingetMatchWindow(WingetCli winget, string appName)
    {
        _winget = winget;
        _initialQuery = appName;
        InitializeComponent();
        AppNameText.Text = appName;
        SearchBox.Text = appName;
        Loaded += async (_, _) =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            await SearchAsync();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                Close();
            }
        };
    }

    public ObservableCollection<WingetPackage> Results { get; } = new();
    public WingetPackage? SelectedPackage { get; private set; }

    private async Task SearchAsync()
    {
        var query = SearchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        SearchStatus.Text = Loc.Get("WingetMatch_SearchingWinget");
        ResultsList.ItemsSource = null;
        EmptyResults.Visibility = Visibility.Collapsed;
        try
        {
            var results = await _winget.SearchAsync(query);
            Results.Clear();
            foreach (var package in results)
            {
                Results.Add(package);
            }

            ResultsList.ItemsSource = Results;
            EmptyResults.Visibility = Results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SearchStatus.Text = Results.Count == 0
                ? Loc.Get("WingetMatch_NoMatchesYouCanPaste")
                : Loc.Plural("WingetMatch_Results", Results.Count);
        }
        catch (Exception ex)
        {
            SearchStatus.Text = Loc.Get("WingetMatch_SearchFailed") + ex.Message;
        }
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync();

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SearchAsync();
        }
    }

    private void ResultsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ResultsList.SelectedItem is WingetPackage package)
        {
            SelectedPackage = package;
            PackageIdBox.Text = package.Id;
        }
    }

    private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Use_Click(sender, e);

    private void Use_Click(object sender, RoutedEventArgs e)
    {
        var id = PackageIdBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(id))
        {
            MessageBox.Show(this, Loc.Get("WingetMatch_SelectAResultOrPaste"), "CTRL+Z",
                MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.OK, AppLanguage.MessageBoxOptions);
            return;
        }

        SelectedPackage = Results.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                          ?? new WingetPackage(SearchBox.Text.Trim(), id, null, null);
        DialogResult = true;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
