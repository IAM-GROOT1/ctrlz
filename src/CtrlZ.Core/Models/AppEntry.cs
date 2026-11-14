using System.Text.Json.Serialization;

namespace CtrlZ.Core.Models;

/// <summary>
/// One installable item inside a profile. The shape is intentionally source-agnostic:
/// only the fields relevant to <see cref="Source"/> are populated, and all transient
/// scan/install state lives outside the profile file.
/// </summary>
public sealed class AppEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = string.Empty;
    public AppSource Source { get; set; } = AppSource.Winget;
    public AppCategory Category { get; set; } = AppCategory.Other;
    public string? Publisher { get; set; }
    public string? Version { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>winget package identifier (or Microsoft Store product id when known).</summary>
    public string? PackageId { get; set; }

    /// <summary>Direct download URL for <see cref="AppSource.DirectUrl"/>.</summary>
    public string? DownloadUrl { get; set; }

    /// <summary>File name override for direct downloads.</summary>
    public string? FileName { get; set; }

    /// <summary>Repository in owner/name form for <see cref="AppSource.GitHub"/>.</summary>
    public string? GitHubRepo { get; set; }

    /// <summary>Exact asset name from the latest release at profile creation time.</summary>
    public string? GitHubAsset { get; set; }

    /// <summary>
    /// If the release asset changes name between versions, this extension is used to pick
    /// the replacement asset at install time. Example: ".exe" or ".zip".
    /// </summary>
    public string? GitHubAssetExtension { get; set; }

    public GitHubAssetKind GitHubAssetKind { get; set; } = GitHubAssetKind.Installer;

    /// <summary>Absolute path for <see cref="AppSource.LocalFile"/>.</summary>
    public string? LocalPath { get; set; }

    /// <summary>Optional silent switches. Empty means CTRL+Z picks defaults by installer type.</summary>
    public string? SilentArgs { get; set; }

    /// <summary>Optional SHA-256 for direct/GitHub downloads. winget has its own manifest hashes.</summary>
    public string? ExpectedSha256 { get; set; }

    public string? InstallLocation { get; set; }

    /// <summary>Icon source discovered during scan. Local path only; never required at restore time.</summary>
    public string? IconPath { get; set; }

    public string? Notes { get; set; }

    [JsonIgnore]
    public string SourceLabel => Source.Display();

    [JsonIgnore]
    public bool HasWingetId => !string.IsNullOrWhiteSpace(PackageId);

    public AppEntry Clone() => new()
    {
        Id = Id,
        Name = Name,
        Source = Source,
        Category = Category,
        Publisher = Publisher,
        Version = Version,
        Enabled = Enabled,
        PackageId = PackageId,
        DownloadUrl = DownloadUrl,
        FileName = FileName,
        GitHubRepo = GitHubRepo,
        GitHubAsset = GitHubAsset,
        GitHubAssetExtension = GitHubAssetExtension,
        GitHubAssetKind = GitHubAssetKind,
        LocalPath = LocalPath,
        SilentArgs = SilentArgs,
        ExpectedSha256 = ExpectedSha256,
        InstallLocation = InstallLocation,
        IconPath = IconPath,
        Notes = Notes
    };

    /// <summary>
    /// A short, human-readable description of the install command. Detailed command
    /// construction remains the installer's job; this is for review UI only.
    /// </summary>
    [JsonIgnore]
    public string InstallSummary => Source switch
    {
        AppSource.Winget => $"winget install --id {PackageId}",
        AppSource.MicrosoftStore => $"Store / winget {PackageId}",
        AppSource.GitHub => $"{GitHubRepo} · latest release{(string.IsNullOrWhiteSpace(GitHubAsset) ? string.Empty : " · " + GitHubAsset)}",
        AppSource.DirectUrl => DownloadUrl ?? "Missing URL",
        AppSource.LocalFile => LocalPath ?? "Missing file",
        _ => "Unknown source"
    };
}
