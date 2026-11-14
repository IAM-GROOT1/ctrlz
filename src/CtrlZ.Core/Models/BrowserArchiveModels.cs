namespace CtrlZ.Core.Models;

public sealed class BrowserBackupManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string App { get; set; } = "CTRL+Z";
    public string AppVersion { get; set; } = "1.1.0";
    public string Format { get; set; } = "browser-profile-backup";
    public BrowserFamily Family { get; set; } = BrowserFamily.Chromium;
    public string BrowserId { get; set; } = string.Empty;
    public string BrowserName { get; set; } = string.Empty;
    public string? BrowserVersion { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public bool PasswordProtected { get; set; }
    public bool FlatProfileLayout { get; set; }
    public List<BrowserBackupProfileManifest> Profiles { get; set; } = new();
    public BrowserBackupContents Contents { get; set; } = new();
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
    public List<string> Warnings { get; set; } = new();
}

public sealed class BrowserBackupProfileManifest
{
    public string DirectoryName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? UserName { get; set; }
    public bool IsDefault { get; set; }
}

public sealed class BrowserBackupContents
{
    public bool LocalState { get; set; }
    public bool Preferences { get; set; }
    public bool Bookmarks { get; set; }
    public bool History { get; set; }
    public bool Sessions { get; set; }
    public bool Extensions { get; set; }
    public bool SearchEngines { get; set; }
    public bool SitePermissions { get; set; }
    public List<string> Excluded { get; set; } = new();
}

public sealed record BrowserBackupRequest(
    DetectedBrowser Browser,
    IReadOnlyList<BrowserProfile> Profiles,
    string DestinationFolder,
    string? Password);

public sealed record BrowserBackupEstimate(
    long TotalBytes,
    int FileCount,
    IReadOnlyList<string> Excluded,
    IReadOnlyList<string> Warnings);

public sealed record BrowserBackupProgress(string Message, int Processed, int Total, double? Fraction);

public sealed record BrowserBackupResult(
    string ArchivePath,
    BrowserBackupManifest Manifest,
    long ArchiveBytes);

public sealed record BrowserArchiveInfo(
    string ArchivePath,
    BrowserBackupManifest Manifest,
    bool IsEncrypted);

public sealed record BrowserRestoreRequest(
    string ArchivePath,
    string? Password,
    DetectedBrowser Target,
    IReadOnlyList<string> ProfileDirectoryNames,
    bool RestoreAsNewProfiles);

public sealed record BrowserRestoreProgress(string Message, int Processed, int Total, double? Fraction);

public sealed record BrowserRestoreResult(
    string BrowserName,
    string UserDataPath,
    IReadOnlyList<string> RestoredProfiles,
    IReadOnlyList<string> Warnings,
    string? SafetyBackupFolder);
