// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

namespace CtrlZ.Core.Tweaks;

public enum TweakValueKind
{
    DWord,
    String
}

/// <summary>
/// One registry value under HKEY_CURRENT_USER. <see cref="SubKey"/> is relative to HKCU and must
/// sit under <c>Software\</c>; nothing outside the current user's hive can be described.
/// </summary>
public sealed record TweakValue(string SubKey, string Name, TweakValueKind Kind, object Value);

/// <summary>A small, reversible Windows setting: a fixed set of per-user registry values.</summary>
public sealed record TweakDefinition(
    string Id,
    string Title,
    string Description,
    IReadOnlyList<TweakValue> Values,
    bool NeedsExplorerRestart,
    bool Windows11Only = false);

/// <summary>
/// The tweaks CTRL+Z offers. Each one is a handful of well-known per-user values, so it needs no
/// admin rights, runs no command, and can be undone exactly. Ids are stable: profiles store them.
/// </summary>
public static class TweakCatalog
{
    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static readonly IReadOnlyList<TweakDefinition> All =
    [
        new("show-file-extensions", "Show file extensions",
            "File Explorer shows .exe, .pdf and other extensions, so a file can't hide what it is.",
            [new(Advanced, "HideFileExt", TweakValueKind.DWord, 0)], NeedsExplorerRestart: true),

        new("show-hidden-files", "Show hidden files",
            "File Explorer shows hidden files and folders such as AppData.",
            [new(Advanced, "Hidden", TweakValueKind.DWord, 1)], NeedsExplorerRestart: true),

        new("explorer-opens-this-pc", "Open File Explorer to This PC",
            "File Explorer starts on your drives instead of Home or Quick access.",
            [new(Advanced, "LaunchTo", TweakValueKind.DWord, 1)], NeedsExplorerRestart: false),

        new("dark-mode", "Dark mode for apps and Windows",
            "Apps and the Windows taskbar and Start use the dark theme.",
            [
                new(Personalize, "AppsUseLightTheme", TweakValueKind.DWord, 0),
                new(Personalize, "SystemUsesLightTheme", TweakValueKind.DWord, 0)
            ], NeedsExplorerRestart: false),

        new("taskbar-left", "Taskbar icons on the left",
            "Windows 11 puts the Start button and taskbar icons on the left, as on Windows 10.",
            [new(Advanced, "TaskbarAl", TweakValueKind.DWord, 0)], NeedsExplorerRestart: false, Windows11Only: true),

        new("no-web-search-in-start", "No web results in Start search",
            "Searching from Start looks only at this PC, not Bing.",
            [new(@"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", TweakValueKind.DWord, 1)],
            NeedsExplorerRestart: true),

        new("classic-context-menu", "Classic right-click menu",
            "Windows 11 shows the full right-click menu at once, without \"Show more options\".",
            [new(@"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32", string.Empty, TweakValueKind.String, string.Empty)],
            NeedsExplorerRestart: true, Windows11Only: true),

        new("no-start-recommendations", "Fewer recommendations in Start",
            "Start stops suggesting tips, shortcuts and new apps.",
            [new(Advanced, "Start_IrisRecommendations", TweakValueKind.DWord, 0)], NeedsExplorerRestart: false, Windows11Only: true)
    ];

    public static TweakDefinition? Find(string? id) =>
        All.FirstOrDefault(tweak => tweak.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>True for a sub-key CTRL+Z may touch: relative, under Software\, no traversal.</summary>
    public static bool IsAllowedSubKey(string subKey) =>
        !string.IsNullOrWhiteSpace(subKey) &&
        subKey.StartsWith(@"Software\", StringComparison.OrdinalIgnoreCase) &&
        !subKey.Contains("..", StringComparison.Ordinal) &&
        !subKey.StartsWith('\\') &&
        !subKey.Contains(':', StringComparison.Ordinal);
}
