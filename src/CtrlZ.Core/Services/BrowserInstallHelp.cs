// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Localization;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Services;

/// <summary>What the restore panel should offer when an archive's own browser is not on this PC.</summary>
public sealed record MissingBrowserAdvice(string BrowserId, string BrowserName, string? WingetId, BrowserFamily Family)
{
    public bool CanInstall => WingetId is not null;

    /// <summary>The steps after installing: a browser creates its profile folder on its first start.</summary>
    public string NextSteps => Family == BrowserFamily.Firefox
        ? CoreText.Format("BrowserInstallHelp_OpenOnceSoItCreates", BrowserName)
        : CoreText.Format("BrowserInstallHelp_OpenOnceSoItCreates2", BrowserName);
}

/// <summary>
/// Helps restore a backup on a PC that never had that browser. CTRL+Z does not build a browser's
/// data folder by hand (Firefox, for one, ties profiles to the installation); it installs the browser
/// through winget, the browser creates its own folder on first start, and the normal restore follows.
/// Only package ids that are well known and also listed in the bundled app catalog are used.
/// </summary>
public static class BrowserInstallHelp
{
    private static readonly IReadOnlyDictionary<string, string> WingetIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = "Google.Chrome",
        ["edge"] = "Microsoft.Edge",
        ["brave"] = "Brave.Brave",
        ["vivaldi"] = "Vivaldi.Vivaldi",
        ["firefox"] = "Mozilla.Firefox",
        ["librewolf"] = "LibreWolf.LibreWolf",
        ["waterfox"] = "Waterfox.Waterfox",
        ["zen"] = "Zen-Team.Zen-Browser"
    };

    public static string? WingetIdFor(string browserId) =>
        WingetIds.TryGetValue(browserId ?? string.Empty, out var id) ? id : null;

    /// <summary>
    /// Advice when the archive's browser is not among <paramref name="detectedIds"/>; null when it is
    /// installed (the normal restore applies).
    /// </summary>
    public static MissingBrowserAdvice? For(BrowserBackupManifest manifest, IEnumerable<string> detectedIds)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (string.IsNullOrWhiteSpace(manifest.BrowserId) ||
            detectedIds.Contains(manifest.BrowserId, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var name = string.IsNullOrWhiteSpace(manifest.BrowserName) ? manifest.BrowserId : manifest.BrowserName;
        return new MissingBrowserAdvice(manifest.BrowserId, name, WingetIdFor(manifest.BrowserId), manifest.Family);
    }
}
