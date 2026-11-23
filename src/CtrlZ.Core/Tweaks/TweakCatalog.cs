// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Localization;

namespace CtrlZ.Core.Tweaks;

public enum TweakValueKind
{
    DWord,
    String
}

/// <summary>
/// One registry value under HKEY_CURRENT_USER. <see cref="SubKey"/> is relative to HKCU and must
/// sit under <c>Software\</c>; nothing outside the current user's hive can be described.
/// <para>
/// <see cref="OwnedBelow"/> names the key above the ones this tweak itself creates (for a value
/// whose key's mere presence matters). Undo without a saved original then also removes those keys
/// once they are empty; without it, only the value is removed.
/// </para>
/// </summary>
public sealed record TweakValue(string SubKey, string Name, TweakValueKind Kind, object Value, string? OwnedBelow = null);

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

    /// <summary>Built on each read so the names follow the language chosen for this run.</summary>
    public static IReadOnlyList<TweakDefinition> All => Build();

    private static IReadOnlyList<TweakDefinition> Build() =>
    [
        new("show-file-extensions", CoreText.Get("Tweak_ShowFileExtensions_Title"),
            CoreText.Get("Tweak_ShowFileExtensions_Description"),
            [new(Advanced, "HideFileExt", TweakValueKind.DWord, 0)], NeedsExplorerRestart: true),

        new("show-hidden-files", CoreText.Get("Tweak_ShowHiddenFiles_Title"),
            CoreText.Get("Tweak_ShowHiddenFiles_Description"),
            [new(Advanced, "Hidden", TweakValueKind.DWord, 1)], NeedsExplorerRestart: true),

        new("explorer-opens-this-pc", CoreText.Get("Tweak_ExplorerOpensThisPc_Title"),
            CoreText.Get("Tweak_ExplorerOpensThisPc_Description"),
            [new(Advanced, "LaunchTo", TweakValueKind.DWord, 1)], NeedsExplorerRestart: false),

        new("dark-mode", CoreText.Get("Tweak_DarkMode_Title"),
            CoreText.Get("Tweak_DarkMode_Description"),
            [
                new(Personalize, "AppsUseLightTheme", TweakValueKind.DWord, 0),
                new(Personalize, "SystemUsesLightTheme", TweakValueKind.DWord, 0)
            ], NeedsExplorerRestart: false),

        new("taskbar-left", CoreText.Get("Tweak_TaskbarLeft_Title"),
            CoreText.Get("Tweak_TaskbarLeft_Description"),
            [new(Advanced, "TaskbarAl", TweakValueKind.DWord, 0)], NeedsExplorerRestart: false, Windows11Only: true),

        new("no-web-search-in-start", CoreText.Get("Tweak_NoWebSearchInStart_Title"),
            CoreText.Get("Tweak_NoWebSearchInStart_Description"),
            // Not the DisableSearchBoxSuggestions policy: HKCU\Software\Policies is read-only for
            // the user, so writing it needs admin rights. This is the per-user switch.
            [new(@"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", TweakValueKind.DWord, 0)],
            NeedsExplorerRestart: true),

        new("classic-context-menu", CoreText.Get("Tweak_ClassicContextMenu_Title"),
            CoreText.Get("Tweak_ClassicContextMenu_Description"),
            [new(@"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32", string.Empty, TweakValueKind.String, string.Empty,
                OwnedBelow: @"Software\Classes\CLSID")],
            NeedsExplorerRestart: true, Windows11Only: true),

        new("no-start-recommendations", CoreText.Get("Tweak_NoStartRecommendations_Title"),
            CoreText.Get("Tweak_NoStartRecommendations_Description"),
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
