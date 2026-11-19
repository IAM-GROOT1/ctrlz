// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Models;

namespace CtrlZ.Core.Services;

/// <summary>Small keyword classifier; accuracy here only affects grouping, not install logic.</summary>
public static class AppClassifier
{
    private static readonly (AppCategory Category, string[] Terms)[] Rules =
    [
        (AppCategory.Browser, ["browser", "chrome", "chromium", "firefox", "edge", "brave", "opera", "vivaldi", "helium", "librewolf", "waterfox", "floorp"]),
        (AppCategory.Development, ["visual studio", "vscode", "vs code", "code", "git", "python", "node", "dotnet", ".net", "jetbrains", "intellij", "pycharm", "webstorm", "docker", "postman", "terminal", "powershell", "sdk", "ide", "unity", "unreal", "android studio", "xcode", "rust", "golang", "mysql", "postgres", "dbeaver", "wireshark", "github"]),
        (AppCategory.Gaming, ["steam", "epic games", "gog galaxy", "battle.net", "riot", "xbox", "ea app", "ubisoft", "playnite", "itch", "gaming", "game"]),
        (AppCategory.Communication, ["discord", "slack", "teams", "zoom", "telegram", "whatsapp", "signal", "skype", "thunderbird", "outlook", "mail", "messenger"]),
        (AppCategory.Media, ["spotify", "vlc", "obs studio", "photoshop", "premiere", "audacity", "foobar", "media player", "mpv", "handbrake", "davinci", "blender", "gimp", "krita", "figma", "canva", "itunes", "music", "video"]),
        (AppCategory.Productivity, ["office", "word", "excel", "powerpoint", "onenote", "notion", "obsidian", "acrobat", "pdf", "libreoffice", "todoist", "evernote", "logseq", "anki", "calibre", "zotero", "notepad", "typora", "joplin"]),
        (AppCategory.Security, ["defender", "malwarebytes", "bitdefender", "avast", "avg", "norton", "1password", "bitwarden", "keepass", "dashlane", "vpn", "nordvpn", "protonvpn", "expressvpn", "password", "security", "firewall"]),
        (AppCategory.Utilities, ["7-zip", "7zip", "winrar", "peazip", "everything", "powertoys", "driver", "ccleaner", "cpu-z", "gpu-z", "hwinfo", "rufus", "ventoy", "sharex", "lightshot", "autohotkey", "quicklook", "files", "terminal", "idm", "internet download manager", "download"]),
    ];

    public static AppCategory Classify(string? name, string? publisher = null)
    {
        var haystack = $"{name} {publisher}".ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(haystack))
        {
            return AppCategory.Other;
        }

        foreach (var (category, terms) in Rules)
        {
            if (terms.Any(haystack.Contains))
            {
                return category;
            }
        }

        return AppCategory.Other;
    }
}
