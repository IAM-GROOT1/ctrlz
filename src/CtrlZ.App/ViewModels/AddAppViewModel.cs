using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CtrlZ.App.Services;
using CtrlZ.Core.Install;
using CtrlZ.Core.Models;
using CtrlZ.Core.Services;
using Microsoft.Win32;

namespace CtrlZ.App.ViewModels;

public enum AddAppMode
{
    Direct,
    GitHub,
    Local
}

public sealed record CategoryOption(AppCategory Category, string Label);

public sealed record AssetKindOption(GitHubAssetKind Kind, string Label);

public sealed record GitHubAssetOption(GitHubAsset Asset)
{
    public string Name => Asset.Name;
    public string Detail => $"{Asset.SizeLabel} · {Asset.Extension.TrimStart('.').ToUpperInvariant()}";
    public GitHubAssetKind SuggestedKind => GitHubReleaseService.SuggestAssetKind(Asset);
}

public sealed partial class AddAppViewModel : PageViewModelBase
{
    private readonly AppServices _services;
    private readonly DialogService _dialogs;
    private readonly MainViewModel _main;
    private string? _inspectedPath;

    public AddAppViewModel(AppServices services, DialogService dialogs, MainViewModel main)
        : base("Add app", "Add installers winget does not carry: a direct link, a GitHub release, or a local file.")
    {
        _services = services;
        _dialogs = dialogs;
        _main = main;
        Categories = Enum.GetValues<AppCategory>()
            .Select(category => new CategoryOption(category, category.Display()))
            .ToArray();
        AssetKinds =
        [
            new(GitHubAssetKind.Installer, "Installer (run silently)"),
            new(GitHubAssetKind.PortableArchive, "Portable archive (extract)"),
            new(GitHubAssetKind.PortableExecutable, "Portable executable (copy)")
        ];
        SelectedCategory = Categories.First(c => c.Category == AppCategory.Productivity);
        SelectedAssetKind = AssetKinds[0];
    }

    public CategoryOption[] Categories { get; }
    public AssetKindOption[] AssetKinds { get; }
    public ObservableCollection<GitHubAssetOption> Assets { get; } = new();

    [ObservableProperty]
    private AddAppMode _mode = AddAppMode.Direct;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _publisher = string.Empty;

    [ObservableProperty]
    private CategoryOption _selectedCategory;

    [ObservableProperty]
    private string _url = string.Empty;

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _repo = string.Empty;

    [ObservableProperty]
    private string _releaseInfo = string.Empty;

    [ObservableProperty]
    private GitHubAssetOption? _selectedAsset;

    [ObservableProperty]
    private AssetKindOption _selectedAssetKind;

    [ObservableProperty]
    private string _localPath = string.Empty;

    [ObservableProperty]
    private string _silentArgs = string.Empty;

    [ObservableProperty]
    private string _hash = string.Empty;

    [ObservableProperty]
    private string _notes = string.Empty;

    [ObservableProperty]
    private bool _isLoadingRelease;

    [ObservableProperty]
    private bool _isDetecting;

    [ObservableProperty]
    private bool _isTesting;

    [ObservableProperty]
    private string _detectedInstaller = "Not inspected yet";

    [ObservableProperty]
    private string _recommendedArgs = string.Empty;

    [ObservableProperty]
    private string _previewCommand = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "Choose a source and fill in what CTRL+Z needs.";

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string _testLog = string.Empty;

    [ObservableProperty]
    private bool _hasTestLog;

    public bool IsDirect => Mode == AddAppMode.Direct;
    public bool IsGitHub => Mode == AddAppMode.GitHub;
    public bool IsLocal => Mode == AddAppMode.Local;

    public bool IsDirectTab
    {
        get => Mode == AddAppMode.Direct;
        set
        {
            if (value) { Mode = AddAppMode.Direct; }
            else if (Mode == AddAppMode.Direct) { OnPropertyChanged(); }
        }
    }

    public bool IsGitHubTab
    {
        get => Mode == AddAppMode.GitHub;
        set
        {
            if (value) { Mode = AddAppMode.GitHub; }
            else if (Mode == AddAppMode.GitHub) { OnPropertyChanged(); }
        }
    }

    public bool IsLocalTab
    {
        get => Mode == AddAppMode.Local;
        set
        {
            if (value) { Mode = AddAppMode.Local; }
            else if (Mode == AddAppMode.Local) { OnPropertyChanged(); }
        }
    }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasAssets => Assets.Count > 0;
    public bool CanTest => _inspectedPath is not null;
    public bool HasRecommended => !string.IsNullOrWhiteSpace(RecommendedArgs);

    public string AddButtonText => $"Add {Name switch { "" => "app", var n => n }} to My Apps";

    partial void OnModeChanged(AddAppMode value)
    {
        OnPropertyChanged(nameof(IsDirect));
        OnPropertyChanged(nameof(IsGitHub));
        OnPropertyChanged(nameof(IsLocal));
        OnPropertyChanged(nameof(IsDirectTab));
        OnPropertyChanged(nameof(IsGitHubTab));
        OnPropertyChanged(nameof(IsLocalTab));
        InvalidateInspection();
        ClearError();
    }

    partial void OnUrlChanged(string value) => InvalidateInspection();

    partial void OnFileNameChanged(string value) => InvalidateInspection();

    partial void OnRepoChanged(string value) => InvalidateInspection();

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(AddButtonText));

    partial void OnSelectedAssetChanged(GitHubAssetOption? value)
    {
        InvalidateInspection();
        if (value is not null)
        {
            OnPropertyChanged(nameof(HasAssets));
            var suggested = AssetKinds.First(k => k.Kind == value.SuggestedKind);
            SelectedAssetKind = suggested;
            if (!string.IsNullOrWhiteSpace(value.Asset.Sha256))
            {
                Hash = value.Asset.Sha256!;
            }

            UpdatePreview();
        }
    }

    partial void OnSelectedAssetKindChanged(AssetKindOption value) => UpdatePreview();

    partial void OnSilentArgsChanged(string value) => UpdatePreview();

    partial void OnLocalPathChanged(string value) => InvalidateInspection();

    [RelayCommand]
    private void SetMode(string? mode)
    {
        Mode = mode?.ToLowerInvariant() switch
        {
            "github" => AddAppMode.GitHub,
            "local" => AddAppMode.Local,
            _ => AddAppMode.Direct
        };
    }

    [RelayCommand]
    private async Task FindReleaseAsync()
    {
        if (!GitHubReleaseService.TryParseRepository(Repo, out var repository, out var error))
        {
            ShowError(error);
            return;
        }

        IsLoadingRelease = true;
        ClearError();
        ReleaseInfo = "Fetching the latest release…";
        try
        {
            var release = await _services.GitHub.GetLatestAsync(repository);
            var ranked = _services.GitHub.RankWindowsAssets(release);
            Assets.Clear();
            foreach (var asset in ranked)
            {
                Assets.Add(new GitHubAssetOption(asset));
            }

            ReleaseInfo = ranked.Count == 0
                ? $"Release {release.TagName} has no supported Windows asset."
                : $"{release.TagName} · {ranked.Count} Windows asset{(ranked.Count == 1 ? string.Empty : "s")} · published {release.PublishedAt?.ToLocalTime():MMM d, yyyy}";
            SelectedAsset = Assets.FirstOrDefault();
            OnPropertyChanged(nameof(HasAssets));
            if (Assets.Count == 0 && string.IsNullOrWhiteSpace(Name))
            {
                Name = repository.Split('/').Last();
            }
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            ReleaseInfo = "Could not load the release.";
        }
        finally
        {
            IsLoadingRelease = false;
        }
    }

    [RelayCommand]
    private void BrowseLocal()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select a local installer",
            Filter = "Installers and archives (*.exe;*.msi;*.msix;*.appx;*.zip)|*.exe;*.msi;*.msix;*.appx;*.zip|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() == true)
        {
            LocalPath = dialog.FileName;
            if (string.IsNullOrWhiteSpace(Name))
            {
                Name = Path.GetFileNameWithoutExtension(dialog.FileName);
            }

            _ = DetectInstallerAsync();
        }
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select a folder containing the installer"
        };
        if (dialog.ShowDialog() == true)
        {
            var candidates = Directory.EnumerateFiles(dialog.FolderName, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => new[] { ".exe", ".msi", ".msix", ".appx", ".zip" }
                    .Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();
            if (candidates.Length == 1)
            {
                LocalPath = candidates[0];
                if (string.IsNullOrWhiteSpace(Name))
                {
                    Name = Path.GetFileNameWithoutExtension(candidates[0]);
                }

                _ = DetectInstallerAsync();
            }
            else
            {
                ShowError(candidates.Length == 0
                    ? "No supported installer was found in that folder."
                    : "More than one installer was found. Choose the file directly.");
            }
        }
    }

    [RelayCommand]
    private async Task DetectInstallerAsync()
    {
        if (IsDetecting)
        {
            return;
        }

        ClearError();
        string? path = null;
        try
        {
            IsDetecting = true;
            DetectedInstaller = "Downloading and inspecting…";
            path = await GetInstallerPathAsync();
            if (path is null)
            {
                return;
            }

            _inspectedPath = path;
            var type = SilentInstallerCatalog.Detect(path);
            var preset = SilentInstallerCatalog.GetPreset(type);
            DetectedInstaller = type == InstallerType.Unknown
                ? "Unrecognized installer"
                : preset.DisplayName;
            RecommendedArgs = preset.Arguments;
            if (string.IsNullOrWhiteSpace(SilentArgs) && !string.IsNullOrWhiteSpace(preset.Arguments))
            {
                SilentArgs = preset.Arguments;
            }

            UpdatePreview();
            StatusMessage = type == InstallerType.Unknown
                ? "Installer type was not recognized. /S is a reasonable starting point, but test it."
                : $"Detected {preset.DisplayName}. Review the switches and test if needed.";
            OnPropertyChanged(nameof(CanTest));
        }
        catch (Exception ex)
        {
            _inspectedPath = null;
            DetectedInstaller = "Inspection failed";
            ShowError(ex.Message);
        }
        finally
        {
            IsDetecting = false;
        }
    }

    [RelayCommand]
    private void UseRecommended()
    {
        if (!string.IsNullOrWhiteSpace(RecommendedArgs))
        {
            SilentArgs = RecommendedArgs;
        }
    }

    [RelayCommand]
    private async Task TestInstallerAsync()
    {
        if (IsTesting)
        {
            return;
        }

        if (_inspectedPath is null)
        {
            await DetectInstallerAsync();
            if (_inspectedPath is null)
            {
                return;
            }
        }

        if (!_dialogs.Confirm("Test this installer",
                "CTRL+Z will run the installer on this PC with the switches shown. " +
                "This is a real installation and may change this machine. Continue?",
                "Run test install", "Cancel", danger: true))
        {
            return;
        }

        IsTesting = true;
        HasTestLog = true;
        TestLog = string.Empty;
        var lines = new ConcurrentQueue<string>();
        var sink = new CaptureSink(lines);
        try
        {
            var app = BuildEntry();
            app.SilentArgs = string.IsNullOrWhiteSpace(SilentArgs) ? RecommendedArgs : SilentArgs;
            var context = new InstallContext(_services.Paths, sink, _services.Log, ElevationService.IsElevated);
            var result = await _services.FileInstaller.RunInstallerAsync(app, _inspectedPath, context, CancellationToken.None);
            TestLog = string.Join(Environment.NewLine, lines.TakeLast(80));
            StatusMessage = result.Success
                ? "Test install succeeded. Review the output, then add the app."
                : "Test install did not complete: " + (result.Error ?? result.Detail);
            if (!result.Success)
            {
                ShowError(StatusMessage);
            }
        }
        catch (Exception ex)
        {
            ShowError("Test install failed: " + ex.Message);
            TestLog = ex.ToString();
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        ClearError();
        if (string.IsNullOrWhiteSpace(Name))
        {
            ShowError("Give the app a name.");
            return;
        }

        var argsError = SilentInstallerCatalog.ValidateCustomArguments(SilentArgs);
        if (argsError is not null)
        {
            ShowError(argsError);
            return;
        }

        if (!string.IsNullOrWhiteSpace(Hash) &&
            (Hash.Trim().Length != 64 || !Hash.Trim().All(Uri.IsHexDigit)))
        {
            ShowError("SHA-256 must be 64 hexadecimal characters, or left blank.");
            return;
        }

        AppEntry entry;
        try
        {
            entry = BuildEntry();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            return;
        }

        var (added, skipped) = _services.State.AddApps([entry]);
        if (added == 0)
        {
            _dialogs.Info("That app is already in this profile.");
            return;
        }

        _dialogs.Info(
            $"Added {entry.Name} to \"{_services.State.ActiveProfile.Name}\"." +
            (skipped > 0 ? $" {skipped} duplicate was skipped." : string.Empty),
            "App added");
        ClearForm();
        await _main.NavigateToAsync("apps");
    }

    [RelayCommand]
    private void ClearForm()
    {
        Name = string.Empty;
        Publisher = string.Empty;
        Url = string.Empty;
        FileName = string.Empty;
        Repo = string.Empty;
        ReleaseInfo = string.Empty;
        Assets.Clear();
        SelectedAsset = null;
        OnPropertyChanged(nameof(HasAssets));
        LocalPath = string.Empty;
        SilentArgs = string.Empty;
        Hash = string.Empty;
        Notes = string.Empty;
        _inspectedPath = null;
        DetectedInstaller = "Not inspected yet";
        RecommendedArgs = string.Empty;
        PreviewCommand = string.Empty;
        TestLog = string.Empty;
        HasTestLog = false;
        StatusMessage = "Choose a source and fill in what CTRL+Z needs.";
        ErrorMessage = null;
    }

    private AppEntry BuildEntry()
    {
        var entry = new AppEntry
        {
            Name = Name.Trim(),
            Publisher = NullIfEmpty(Publisher),
            Category = SelectedCategory.Category,
            SilentArgs = NullIfEmpty(SilentArgs),
            ExpectedSha256 = NullIfEmpty(Hash),
            Notes = NullIfEmpty(Notes),
            Enabled = true
        };

        switch (Mode)
        {
            case AddAppMode.Direct:
                if (!Uri.TryCreate(Url.Trim(), UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    throw new InvalidOperationException("Enter a valid absolute http(s) download URL.");
                }

                entry.Source = AppSource.DirectUrl;
                entry.DownloadUrl = Url.Trim();
                entry.FileName = NullIfEmpty(FileName);
                break;

            case AddAppMode.GitHub:
                if (!GitHubReleaseService.TryParseRepository(Repo, out var repository, out var repoError))
                {
                    throw new InvalidOperationException(repoError);
                }

                entry.Source = AppSource.GitHub;
                entry.GitHubRepo = repository;
                entry.GitHubAsset = SelectedAsset?.Asset.Name;
                entry.GitHubAssetExtension = SelectedAsset?.Asset.Extension;
                entry.GitHubAssetKind = SelectedAssetKind.Kind;
                break;

            case AddAppMode.Local:
                if (string.IsNullOrWhiteSpace(LocalPath))
                {
                    throw new InvalidOperationException("Choose the local installer file.");
                }

                if (!File.Exists(LocalPath))
                {
                    throw new InvalidOperationException("That local installer no longer exists.");
                }

                entry.Source = AppSource.LocalFile;
                entry.LocalPath = LocalPath;
                break;
        }

        return entry;
    }

    private async Task<string?> GetInstallerPathAsync()
    {
        switch (Mode)
        {
            case AddAppMode.Local:
                if (!File.Exists(LocalPath))
                {
                    ShowError("Choose an existing local installer file.");
                    return null;
                }

                return LocalPath;

            case AddAppMode.Direct:
                if (!Uri.TryCreate(Url.Trim(), UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    ShowError("Enter a valid download URL first.");
                    return null;
                }

                var downloaded = await _services.Downloads.DownloadAsync(
                    Url.Trim(), NullIfEmpty(FileName), NullIfEmpty(Hash),
                    new Progress<DownloadProgress>(p =>
                        StatusMessage = p.TotalBytes is > 0
                            ? $"Inspecting installer… {p.Fraction:P0}"
                            : "Inspecting installer…"));
                return downloaded.Path;

            case AddAppMode.GitHub:
                if (SelectedAsset is null)
                {
                    ShowError("Find a release and choose an asset first.");
                    return null;
                }

                var assetDownload = await _services.Downloads.DownloadAsync(
                    SelectedAsset.Asset.DownloadUrl,
                    SelectedAsset.Asset.Name,
                    SelectedAsset.Asset.Sha256 ?? NullIfEmpty(Hash),
                    new Progress<DownloadProgress>(p =>
                        StatusMessage = p.TotalBytes is > 0
                            ? $"Inspecting {SelectedAsset.Name}… {p.Fraction:P0}"
                            : "Inspecting asset…"));
                return assetDownload.Path;
        }

        return null;
    }

    private void UpdatePreview()
    {
        if (string.IsNullOrWhiteSpace(SilentArgs))
        {
            SilentArgs = RecommendedArgs;
        }

        if (_inspectedPath is null || string.IsNullOrWhiteSpace(SilentArgs))
        {
            PreviewCommand = string.Empty;
            OnPropertyChanged(nameof(HasRecommended));
            return;
        }

        PreviewCommand = SilentInstallerCatalog.BuildDisplayCommand(_inspectedPath, SilentArgs);
        OnPropertyChanged(nameof(HasRecommended));
    }

    private void InvalidateInspection()
    {
        _inspectedPath = null;
        DetectedInstaller = "Not inspected yet";
        RecommendedArgs = string.Empty;
        PreviewCommand = string.Empty;
        OnPropertyChanged(nameof(CanTest));
        OnPropertyChanged(nameof(HasRecommended));
    }

    private void ShowError(string message)
    {
        ErrorMessage = message;
    }

    private void ClearError() => ErrorMessage = null;

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class CaptureSink : IInstallLogSink
    {
        private readonly ConcurrentQueue<string> _lines;
        public CaptureSink(ConcurrentQueue<string> lines) => _lines = lines;
        public void Line(string message) => _lines.Enqueue(message);
        public void Progress(InstallProgress progress) { }
    }
}
