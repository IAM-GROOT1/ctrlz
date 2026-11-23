// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Localization;

namespace CtrlZ.Core.Models;

/// <summary>
/// A browser fork is just configuration. Adding a new fork means adding one definition
/// to <see cref="Services.ChromiumBrowserCatalog"/> or
/// <see cref="Services.FirefoxBrowserCatalog"/>; detector, backup, and restore dispatch by
/// <see cref="BrowserFamily"/>.
/// </summary>
public sealed record BrowserAppDefinition(
    string Id,
    string DisplayName,
    string[] UserDataCandidates,
    string[] ExecutableCandidates,
    string[] ProcessNames,
    string[] RegistryNamePatterns,
    bool FlatProfileLayout = false,
    bool IsBuiltIn = true,
    BrowserFamily Family = BrowserFamily.Chromium);

public sealed record BrowserProfile(
    string DirectoryName,
    string DisplayName,
    string? UserName,
    bool IsDefault,
    string DirectoryPath)
{
    public bool IsFlat => DirectoryName == ".";
    public string Display => string.IsNullOrWhiteSpace(DisplayName) ? DirectoryName : DisplayName;
}

public sealed class DetectedBrowser
{
    public BrowserAppDefinition Definition { get; init; } = null!;
    public string Id => Definition.Id;
    public BrowserFamily Family => Definition.Family;
    public string FamilyLabel => Family == BrowserFamily.Firefox ? "Firefox" : "Chromium";
    public string DisplayName { get; init; } = string.Empty;
    public string? UserDataPath { get; set; }
    public string? ExecutablePath { get; set; }
    public string? Version { get; set; }
    public bool IsCustom { get; init; }
    public bool FoundInRegistry { get; init; }
    public bool NeedsUserDataPath => string.IsNullOrWhiteSpace(UserDataPath);
    public List<BrowserProfile> Profiles { get; set; } = new();
    public bool CanBackup => !NeedsUserDataPath && Profiles.Count > 0;

    public string StatusText => NeedsUserDataPath
        ? CoreText.Get("BrowserModels_ProfileFolderNotFound")
        : Profiles.Count == 0
            ? CoreText.Get("BrowserModels_NoProfilesFound")
            : $"{Profiles.Count} profile{(Profiles.Count == 1 ? string.Empty : "s")}";
}

public sealed class BrowserRunningException : Exception
{
    public BrowserRunningException(string browserName, IReadOnlyList<string> processes)
        : base(CoreText.Format("BrowserModels_IsStillRunningCloseIt", browserName, string.Join(", ", processes)))
    {
        BrowserName = browserName;
        Processes = processes;
    }

    public string BrowserName { get; }
    public IReadOnlyList<string> Processes { get; }
}

public sealed class BrowserPathValidationException : Exception
{
    public BrowserPathValidationException(string message) : base(message) { }
}

public sealed class BrowserArchivePasswordException : Exception
{
    public BrowserArchivePasswordException(string message) : base(message) { }
}

public sealed class BrowserBackupLockedException : Exception
{
    public BrowserBackupLockedException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class BrowserBackupException : Exception
{
    public BrowserBackupException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class BrowserRestoreConflictException : Exception
{
    public BrowserRestoreConflictException(IReadOnlyList<string> profiles)
        : base(CoreText.Get("BrowserModels_TheseProfilesAlreadyExistChoose"))
    {
        Profiles = profiles;
    }

    public IReadOnlyList<string> Profiles { get; }
}
