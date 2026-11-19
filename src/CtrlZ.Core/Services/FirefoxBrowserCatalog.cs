// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Models;

namespace CtrlZ.Core.Services;

/// <summary>
/// Firefox-family browser roots. Firefox forks share the Gecko profile format
/// (profiles.ini + Profiles/&lt;name&gt;), but their roaming folder names differ.
/// </summary>
public static class FirefoxBrowserCatalog
{
    private const string Roaming = "%APPDATA%";
    private const string Local = "%LOCALAPPDATA%";
    private const string ProgramFiles = "%ProgramFiles%";
    private const string ProgramFilesX86 = "%ProgramFiles(x86)%";
    private const string UserProfile = "%USERPROFILE%";

    public static readonly IReadOnlyList<BrowserAppDefinition> Definitions =
    [
        new(
            "firefox", "Mozilla Firefox",
            [$@"{Roaming}\Mozilla\Firefox"],
            [$@"{ProgramFiles}\Mozilla Firefox\firefox.exe",
             $@"{ProgramFilesX86}\Mozilla Firefox\firefox.exe",
             $@"{Local}\Mozilla Firefox\firefox.exe"],
            ["firefox"],
            ["Mozilla Firefox", "Firefox"],
            Family: BrowserFamily.Firefox),

        new(
            "zen", "Zen Browser",
            [$@"{Roaming}\zen", $@"{Roaming}\Zen", $@"{Local}\Zen", $@"{Local}\zen"],
            [$@"{Local}\Programs\Zen Browser\zen.exe",
             $@"{ProgramFiles}\Zen Browser\zen.exe",
             $@"{ProgramFiles}\Zen\zen.exe",
             $@"{ProgramFiles}\Zen Browser\zen.exe"],
            ["zen"],
            ["Zen Browser", "Zen"],
            Family: BrowserFamily.Firefox),

        new(
            "librewolf", "LibreWolf",
            [$@"{Roaming}\LibreWolf", $@"{Roaming}\librewolf", $@"{Local}\LibreWolf"],
            [$@"{ProgramFiles}\LibreWolf\librewolf.exe",
             $@"{ProgramFiles}\LibreWolf\LibreWolf.exe",
             $@"{Local}\LibreWolf\librewolf.exe",
             $@"{Local}\Programs\LibreWolf\librewolf.exe"],
            ["librewolf"],
            ["LibreWolf"],
            Family: BrowserFamily.Firefox),

        new(
            "waterfox", "Waterfox",
            [$@"{Roaming}\Waterfox", $@"{Local}\Waterfox"],
            [$@"{ProgramFiles}\Waterfox\waterfox.exe",
             $@"{ProgramFiles}\Waterfox\Waterfox.exe",
             $@"{Local}\Waterfox\waterfox.exe"],
            ["waterfox"],
            ["Waterfox"],
            Family: BrowserFamily.Firefox),

        new(
            "floorp", "Floorp",
            [$@"{Roaming}\Floorp", $@"{Local}\Floorp"],
            [$@"{ProgramFiles}\Floorp\floorp.exe",
             $@"{ProgramFiles}\Floorp\Floorp.exe",
             $@"{Local}\Programs\Floorp\floorp.exe"],
            ["floorp"],
            ["Floorp"],
            Family: BrowserFamily.Firefox),

        new(
            "mullvad", "Mullvad Browser",
            [$@"{Roaming}\Mullvad\MullvadBrowser", $@"{Local}\Mullvad\MullvadBrowser"],
            [$@"{ProgramFiles}\Mullvad\Mullvad Browser\Browser\mullvad-browser.exe",
             $@"{ProgramFiles}\Mullvad\Mullvad Browser\Browser\firefox.exe",
             $@"{ProgramFiles}\Mullvad Browser\Browser\mullvad-browser.exe",
             $@"{Local}\Programs\Mullvad Browser\Browser\mullvad-browser.exe"],
            ["mullvad-browser", "firefox"],
            ["Mullvad Browser"],
            Family: BrowserFamily.Firefox),

        new(
            "tor", "Tor Browser",
            [$@"{ProgramFiles}\Tor Browser\Browser\TorBrowser\Data\Browser",
             $@"{Local}\Tor Browser\Browser\TorBrowser\Data\Browser",
             $@"{UserProfile}\Desktop\Tor Browser\Browser\TorBrowser\Data\Browser",
             $@"{UserProfile}\Downloads\Tor Browser\Browser\TorBrowser\Data\Browser"],
            [$@"{ProgramFiles}\Tor Browser\Browser\firefox.exe",
             $@"{Local}\Tor Browser\Browser\firefox.exe",
             $@"{UserProfile}\Desktop\Tor Browser\Browser\firefox.exe",
             $@"{UserProfile}\Downloads\Tor Browser\Browser\firefox.exe"],
            ["firefox", "tor"],
            ["Tor Browser"],
            Family: BrowserFamily.Firefox)
    ];

    public static BrowserAppDefinition? Find(string id) =>
        Definitions.FirstOrDefault(definition => definition.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>All browsers supported by the detector, independent of engine.</summary>
public static class BrowserCatalog
{
    public static readonly IReadOnlyList<BrowserAppDefinition> Definitions =
        ChromiumBrowserCatalog.Definitions.Concat(FirefoxBrowserCatalog.Definitions).ToArray();

    public static BrowserAppDefinition? Find(string id) =>
        Definitions.FirstOrDefault(definition => definition.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
