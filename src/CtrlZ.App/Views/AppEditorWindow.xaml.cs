// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.IO;
using System.Windows;
using System.Windows.Controls;
using CtrlZ.Core.Install;
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
    private readonly bool _canEditCommand;
    private readonly Func<PostInstallCommand, bool>? _confirmRisky;

    /// <param name="app">The app to edit; a copy is edited.</param>
    /// <param name="canEditCommand">True only for a profile that originated on this PC.</param>
    /// <param name="confirmRisky">Second confirmation for a risky command; without it risky commands cannot be saved.</param>
    public AppEditorWindow(AppEntry app, bool canEditCommand = false, Func<PostInstallCommand, bool>? confirmRisky = null)
    {
        _working = app.Clone();
        _canEditCommand = canEditCommand;
        _confirmRisky = confirmRisky;
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
        LoadCommand(app.PostInstall);
        UpdatePanels();
    }

    private void LoadCommand(PostInstallCommand? command)
    {
        CommandEditPanel.Visibility = _canEditCommand ? Visibility.Visible : Visibility.Collapsed;
        CommandReadOnlyPanel.Visibility = _canEditCommand ? Visibility.Collapsed : Visibility.Visible;
        CommandReadOnlyText.Text = command?.Describe() ?? "No command.";

        var input = PostInstallCommandForm.From(command);
        CommandExeBox.Text = input.Executable;
        CommandArgsBox.Text = input.ArgumentsText;
        CommandDirBox.Text = input.WorkingDirectory;
        CommandTimeoutBox.Text = input.TimeoutText;
        CommandElevatedBox.IsChecked = input.RequiresElevation;
        CommandEnabledBox.IsChecked = input.Enabled;
    }

    /// <summary>Reads the command fields. Returns false with an error shown when they are invalid or not confirmed.</summary>
    private bool TryReadCommand(out PostInstallCommand? command)
    {
        command = _working.PostInstall;
        if (!_canEditCommand)
        {
            // Untrusted profiles keep their command untouched; it is reviewed elsewhere.
            return true;
        }

        var parsed = PostInstallCommandForm.Parse(new PostInstallCommandInput(
            CommandExeBox.Text,
            CommandArgsBox.Text,
            CommandDirBox.Text,
            CommandTimeoutBox.Text,
            CommandElevatedBox.IsChecked == true,
            CommandEnabledBox.IsChecked == true));

        if (!parsed.IsValid)
        {
            ShowError(parsed.Error!);
            return false;
        }

        if (PostInstallCommandForm.NeedsRiskConfirmation(_working.PostInstall, parsed.Command) &&
            _confirmRisky?.Invoke(parsed.Command!) != true)
        {
            ShowError("The command was not saved: it matches a risky pattern and was not confirmed.");
            return false;
        }

        command = parsed.Command;
        return true;
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

        if (!TryReadCommand(out var command))
        {
            return;
        }

        _working.PostInstall = command;

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
