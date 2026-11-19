// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.IO;
using System.Windows;
using System.Windows.Controls;
using CtrlZ.Core.Models;

namespace CtrlZ.App.Views;

public partial class AppEditorWindow : Window
{
    private static readonly SourceOption[] SourceOptions =
    [
        new(AppSource.Winget, "winget"),
        new(AppSource.MicrosoftStore, "Microsoft Store"),
        new(AppSource.GitHub, "GitHub release"),
        new(AppSource.DirectUrl, "Direct download link"),
        new(AppSource.LocalFile, "Local installer file")
    ];

    private static readonly AssetKindOption[] AssetOptions =
    [
        new(GitHubAssetKind.Installer, "Installer"),
        new(GitHubAssetKind.PortableArchive, "Portable archive (ZIP)"),
        new(GitHubAssetKind.PortableExecutable, "Portable executable")
    ];

    private readonly AppEntry _working;

    public AppEditorWindow(AppEntry app)
    {
        _working = app.Clone();
        InitializeComponent();
        SourceBox.ItemsSource = SourceOptions;
        SourceBox.SelectedItem = SourceOptions.FirstOrDefault(s => s.Source == app.Source);
        CategoryBox.ItemsSource = Enum.GetValues<AppCategory>()
            .Select(c => new CategoryOption(c, c.Display()))
            .ToArray();
        CategoryBox.SelectedItem = ((IEnumerable<CategoryOption>)CategoryBox.ItemsSource)
            .FirstOrDefault(c => c.Category == app.Category);
        AssetKindBox.ItemsSource = AssetOptions;
        AssetKindBox.SelectedItem = AssetOptions.FirstOrDefault(a => a.Kind == app.GitHubAssetKind);

        NameBox.Text = app.Name;
        PublisherBox.Text = app.Publisher ?? string.Empty;
        PackageIdBox.Text = app.PackageId ?? string.Empty;
        UrlBox.Text = app.DownloadUrl ?? string.Empty;
        FileNameBox.Text = app.FileName ?? string.Empty;
        RepoBox.Text = app.GitHubRepo ?? string.Empty;
        AssetBox.Text = app.GitHubAsset ?? string.Empty;
        LocalPathBox.Text = app.LocalPath ?? string.Empty;
        SilentArgsBox.Text = app.SilentArgs ?? string.Empty;
        HashBox.Text = app.ExpectedSha256 ?? string.Empty;
        NotesBox.Text = app.Notes ?? string.Empty;
        EnabledBox.IsChecked = app.Enabled;
        UpdatePanels();
    }

    public AppEntry? Result { get; private set; }

    private void SourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePanels();

    private void UpdatePanels()
    {
        if (WingetPanel is null || SourceBox.SelectedItem is not SourceOption option)
        {
            return;
        }

        WingetPanel.Visibility = option.Source is AppSource.Winget or AppSource.MicrosoftStore
            ? Visibility.Visible
            : Visibility.Collapsed;
        DirectPanel.Visibility = option.Source == AppSource.DirectUrl ? Visibility.Visible : Visibility.Collapsed;
        GitHubPanel.Visibility = option.Source == AppSource.GitHub ? Visibility.Visible : Visibility.Collapsed;
        LocalPanel.Visibility = option.Source == AppSource.LocalFile ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select an installer",
            Filter = "Installers (*.exe;*.msi;*.msix;*.appx;*.zip)|*.exe;*.msi;*.msix;*.appx;*.zip|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() == true)
        {
            LocalPathBox.Text = dialog.FileName;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            ShowError("Give the app a name.");
            return;
        }

        if (SourceBox.SelectedItem is not SourceOption source)
        {
            ShowError("Choose an install source.");
            return;
        }

        var argsError = CtrlZ.Core.Services.SilentInstallerCatalog.ValidateCustomArguments(SilentArgsBox.Text);
        if (argsError is not null)
        {
            ShowError(argsError);
            return;
        }

        var hash = HashBox.Text.Trim();
        if (hash.Length > 0 && (hash.Length != 64 || !hash.All(Uri.IsHexDigit)))
        {
            ShowError("SHA-256 must be 64 hexadecimal characters, or left blank.");
            return;
        }

        if (source.Source is AppSource.Winget or AppSource.MicrosoftStore && string.IsNullOrWhiteSpace(PackageIdBox.Text))
        {
            ShowError("Enter the winget package id for this source.");
            return;
        }

        if (source.Source == AppSource.DirectUrl && !Uri.TryCreate(UrlBox.Text.Trim(), UriKind.Absolute, out _))
        {
            ShowError("Enter a valid absolute http(s) download URL.");
            return;
        }

        if (source.Source == AppSource.GitHub &&
            !CtrlZ.Core.Services.GitHubReleaseService.TryParseRepository(RepoBox.Text, out _, out var repoError))
        {
            ShowError(repoError);
            return;
        }

        if (source.Source == AppSource.LocalFile && string.IsNullOrWhiteSpace(LocalPathBox.Text))
        {
            ShowError("Choose the local installer file.");
            return;
        }

        _working.Name = NameBox.Text.Trim();
        _working.Publisher = NullIfEmpty(PublisherBox.Text);
        _working.Source = source.Source;
        _working.Category = (CategoryBox.SelectedItem as CategoryOption)?.Category ?? AppCategory.Other;
        _working.PackageId = NullIfEmpty(PackageIdBox.Text);
        _working.DownloadUrl = NullIfEmpty(UrlBox.Text);
        _working.FileName = NullIfEmpty(FileNameBox.Text);
        _working.GitHubRepo = NullIfEmpty(RepoBox.Text);
        _working.GitHubAsset = NullIfEmpty(AssetBox.Text);
        _working.GitHubAssetExtension = Path.GetExtension(_working.GitHubAsset ?? string.Empty);
        _working.GitHubAssetKind = (AssetKindBox.SelectedItem as AssetKindOption)?.Kind ?? GitHubAssetKind.Installer;
        _working.LocalPath = NullIfEmpty(LocalPathBox.Text);
        _working.SilentArgs = NullIfEmpty(SilentArgsBox.Text);
        _working.ExpectedSha256 = NullIfEmpty(HashBox.Text);
        _working.Notes = NullIfEmpty(NotesBox.Text);
        _working.Enabled = EnabledBox.IsChecked == true;

        Result = _working;
        DialogResult = true;
        Close();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record SourceOption(AppSource Source, string Label);
    private sealed record CategoryOption(AppCategory Category, string Label);
    private sealed record AssetKindOption(GitHubAssetKind Kind, string Label);
}
