// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

namespace CtrlZ.Core.Services;

/// <summary>
/// Whitelist-first backup policy. It keeps bookmarks, history, sessions, extensions,
/// preferences, search engines, and site permissions, while never including credentials,
/// cookies, or page caches.
/// </summary>
public static class BrowserDataRules
{
    public static readonly string[] ProfileFiles =
    [
        "Preferences",
        "Secure Preferences",
        "Managed Preferences",
        "Bookmarks",
        "Bookmarks.bak",
        "History",
        "Favicons",
        "Top Sites",
        "Visited Links",
        "Web Data",
        "Current Session",
        "Current Tabs",
        "Last Session",
        "Last Tabs",
        "Shortcuts"
    ];

    public static readonly string[] ProfileDirectories =
    [
        "Sessions",
        "Extensions",
        "Extension State",
        "Local Extension Settings",
        "Sync Extension Settings",
        "Managed Extension Settings",
        "Extension Rules",
        "Extension Scripts",
        "Extension Activity",
        "Session Storage"
    ];

    /// <summary>Junk that is intentionally never archived.</summary>
    public static readonly string[] ExcludedDirectoryNames =
    [
        "Cache",
        "Code Cache",
        "GPUCache",
        "ShaderCache",
        "GrShaderCache",
        "GraphiteDawnCache",
        "DawnCache",
        "DawnGraphiteCache",
        "Service Worker",
        "Crashpad",
        "Crash Reports",
        "component_crx_cache",
        "extensions_crx_cache",
        "Safe Browsing",
        "optimization_guide_model_store",
        "segmentation_platform",
        "Blob Storage",
        "File System",
        "IndexedDB",
        "Local Storage"
    ];

    /// <summary>Credential/cookie and lock artifacts that must never be written to an archive.</summary>
    public static readonly string[] ForbiddenFileNames =
    [
        "Login Data",
        "Login Data For Account",
        "Login Data-journal",
        "Cookies",
        "Cookies-journal",
        "Network Persistent State",
        "TransportSecurity",
        "Reporting and NEL",
        "SCT Auditing Pending Reports",
        "Extension Cookies",
        "Extension Cookies-journal",
        "Affiliation Database",
        "Sync Data",
        "SingletonLock",
        "SingletonCookie",
        "SingletonSocket",
        "lockfile",
        "LOG",
        "LOG.old"
    ];

    public static bool IsExcludedDirectory(string name) =>
        ExcludedDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    public static bool IsForbiddenFile(string name)
    {
        if (ForbiddenFileNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.EndsWith("-journal", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Crashpad", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Credential stores sometimes have platform suffixes.
        return name.StartsWith("Login Data", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("Cookies", StringComparison.OrdinalIgnoreCase);
    }
}
