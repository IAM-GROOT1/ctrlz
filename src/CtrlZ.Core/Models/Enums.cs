// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

namespace CtrlZ.Core.Models;

/// <summary>Where an entry comes from and how it should be installed.</summary>
public enum AppSource
{
    Winget,
    MicrosoftStore,
    GitHub,
    DirectUrl,
    LocalFile
}

/// <summary>
/// Broad groups used by the scan view and profile. Deliberately small: the UI should
/// never become a taxonomy project. Unknown catalog values fall back to Other.
/// </summary>
public enum AppCategory
{
    Browser,
    Communication,
    Development,
    Gaming,
    Media,
    Productivity,
    Security,
    Utilities,
    Other
}

public enum WingetMatchKind
{
    None,
    Exact,
    Strong,
    Weak,
    Manual
}

public enum BrowserFamily
{
    Chromium,
    Firefox
}

public enum ThemeMode
{
    System,
    Light,
    Dark
}

public enum InstallStatus
{
    Queued,
    Preparing,
    Downloading,
    Installing,
    Success,
    Failed,
    NeedsManualAction,
    RebootRequired,
    Skipped,
    /// <summary>Installed, but its optional post-install command failed. Never blocks the queue.</summary>
    PostInstallFailed,
    Cancelled
}

/// <summary>How a downloaded GitHub asset should be treated.</summary>
public enum GitHubAssetKind
{
    Installer,
    PortableArchive,
    PortableExecutable
}

/// <summary>Which releases the updater is allowed to offer.</summary>
public enum UpdateChannel
{
    /// <summary>Only full releases; pre-releases are ignored.</summary>
    Stable,

    /// <summary>Full releases and pre-releases, whichever is newest.</summary>
    PreRelease
}

public static class CategoryNames
{
    public static string Display(this AppCategory category) => category switch
    {
        AppCategory.Browser => "Browsers",
        AppCategory.Communication => "Communication",
        AppCategory.Development => "Development",
        AppCategory.Gaming => "Gaming",
        AppCategory.Media => "Media",
        AppCategory.Productivity => "Productivity",
        AppCategory.Security => "Security",
        AppCategory.Utilities => "Utilities",
        _ => "Other"
    };

    public static string Display(this AppSource source) => source switch
    {
        AppSource.Winget => "winget",
        AppSource.MicrosoftStore => "Microsoft Store",
        AppSource.GitHub => "GitHub",
        AppSource.DirectUrl => "Direct link",
        AppSource.LocalFile => "Local file",
        _ => source.ToString()
    };

    public static AppCategory ParseCategory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return AppCategory.Other;
        }

        var normalized = value.Trim();
        return normalized.ToLowerInvariant() switch
        {
            "browsers" or "browser" => AppCategory.Browser,
            "communications" or "communication" => AppCategory.Communication,
            "development" or "developer" or "programming" => AppCategory.Development,
            "games" or "gaming" => AppCategory.Gaming,
            "media" or "multimedia" => AppCategory.Media,
            "productivity" or "office" => AppCategory.Productivity,
            "security" or "privacy" => AppCategory.Security,
            "utilities" or "utility" or "drivers" or "runtimes" or "tools" => AppCategory.Utilities,
            _ => Enum.TryParse<AppCategory>(normalized, ignoreCase: true, out var parsed)
                ? parsed
                : AppCategory.Other
        };
    }
}
