// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Models;

namespace CtrlZ.Core.Services;

/// <summary>
/// Per-fork configuration. Keep this list boring and declarative. A new Chromium fork
/// should only need a new entry here (plus optional tests for its User Data layout).
/// </summary>
public static class ChromiumBrowserCatalog
{
    private const string LocalAppData = "%LOCALAPPDATA%";
    private const string RoamingAppData = "%APPDATA%";
    private const string ProgramFiles = "%ProgramFiles%";
    private const string ProgramFilesX86 = "%ProgramFiles(x86)%";

    public static readonly IReadOnlyList<BrowserAppDefinition> Definitions =
    [
        new(
            "chrome", "Google Chrome",
            [$@"{LocalAppData}\Google\Chrome\User Data"],
            [$@"{ProgramFiles}\Google\Chrome\Application\chrome.exe",
             $@"{ProgramFiles}\Google\Chrome\Application\*\chrome.exe",
             $@"{ProgramFilesX86}\Google\Chrome\Application\chrome.exe",
             $@"{ProgramFilesX86}\Google\Chrome\Application\*\chrome.exe",
             $@"{LocalAppData}\Google\Chrome\Application\chrome.exe",
             $@"{LocalAppData}\Google\Chrome\Application\*\chrome.exe"],
            ["chrome"],
            ["Google Chrome"]),

        new(
            "edge", "Microsoft Edge",
            [$@"{LocalAppData}\Microsoft\Edge\User Data"],
            [$@"{ProgramFilesX86}\Microsoft\Edge\Application\msedge.exe",
             $@"{ProgramFilesX86}\Microsoft\Edge\Application\*\msedge.exe",
             $@"{ProgramFiles}\Microsoft\Edge\Application\msedge.exe",
             $@"{ProgramFiles}\Microsoft\Edge\Application\*\msedge.exe"],
            ["msedge"],
            ["Microsoft Edge"]),

        new(
            "brave", "Brave",
            [$@"{LocalAppData}\BraveSoftware\Brave-Browser\User Data"],
            [$@"{ProgramFiles}\BraveSoftware\Brave-Browser\Application\brave.exe",
             $@"{ProgramFiles}\BraveSoftware\Brave-Browser\Application\*\brave.exe",
             $@"{ProgramFilesX86}\BraveSoftware\Brave-Browser\Application\brave.exe",
             $@"{ProgramFilesX86}\BraveSoftware\Brave-Browser\Application\*\brave.exe",
             $@"{LocalAppData}\BraveSoftware\Brave-Browser\Application\brave.exe",
             $@"{LocalAppData}\BraveSoftware\Brave-Browser\Application\*\brave.exe"],
            ["brave"],
            ["Brave"]),

        new(
            "vivaldi", "Vivaldi",
            [$@"{LocalAppData}\Vivaldi\User Data"],
            [$@"{LocalAppData}\Vivaldi\Application\vivaldi.exe",
             $@"{ProgramFiles}\Vivaldi\Application\vivaldi.exe"],
            ["vivaldi"],
            ["Vivaldi"]),

        new(
            "opera", "Opera",
            [$@"{RoamingAppData}\Opera Software\Opera Stable"],
            [$@"{LocalAppData}\Programs\Opera\opera.exe",
             $@"{ProgramFiles}\Opera\opera.exe"],
            ["opera"],
            ["Opera"],
            FlatProfileLayout: true),

        new(
            "opera-gx", "Opera GX",
            [$@"{RoamingAppData}\Opera Software\Opera GX Stable"],
            [$@"{LocalAppData}\Programs\Opera GX\opera.exe",
             $@"{LocalAppData}\Programs\Opera\opera.exe"],
            ["opera"],
            ["Opera GX"],
            FlatProfileLayout: true),

        new(
            "helium", "Helium",
            [$@"{LocalAppData}\net.imput.helium\User Data",
             $@"{LocalAppData}\imput\Helium\User Data",
             $@"{LocalAppData}\Helium\User Data"],
            [$@"{LocalAppData}\imput\Helium\Application\chrome.exe",
             $@"{LocalAppData}\imput\Helium\Application\helium.exe",
             $@"{LocalAppData}\net.imput.helium\Application\helium.exe",
             $@"{ProgramFiles}\Helium\Application\chrome.exe"],
            ["helium", "chrome"],
            ["Helium"]),

        new(
            "chromium", "Chromium",
            [$@"{LocalAppData}\Chromium\User Data"],
            [$@"{LocalAppData}\Chromium\Application\chrome.exe",
             $@"{LocalAppData}\Chromium\Application\*\chrome.exe",
             $@"{ProgramFiles}\Chromium\Application\chrome.exe",
             $@"{ProgramFiles}\Chromium\Application\*\chrome.exe"],
            ["chrome"],
            ["Chromium"]),

        new(
            "thorium", "Thorium",
            [$@"{LocalAppData}\Thorium\User Data"],
            [$@"{LocalAppData}\Thorium\Application\thorium.exe",
             $@"{ProgramFiles}\Thorium\Application\thorium.exe"],
            ["thorium"],
            ["Thorium"]),

        new(
            "yandex", "Yandex Browser",
            [$@"{LocalAppData}\Yandex\YandexBrowser\User Data"],
            [$@"{LocalAppData}\Yandex\YandexBrowser\Application\browser.exe"],
            ["browser"],
            ["Yandex"]),

        new(
            "comet", "Perplexity Comet",
            [$@"{LocalAppData}\Perplexity\Comet\User Data",
             $@"{LocalAppData}\Comet\User Data"],
            [$@"{LocalAppData}\Perplexity\Comet\Application\comet.exe",
             $@"{LocalAppData}\Programs\Comet\Comet.exe"],
            ["comet"],
            ["Comet"]),

        new(
            "arc", "Arc",
            [$@"{LocalAppData}\Packages\TheBrowserCompany.Arc_*\LocalCache\Local\Arc\User Data"],
            [$@"{LocalAppData}\Programs\Arc\Arc.exe"],
            ["Arc"],
            ["Arc"]),

        new(
            "whale", "Naver Whale",
            [$@"{LocalAppData}\Naver\Naver Whale\User Data"],
            [$@"{LocalAppData}\Naver\Naver Whale\Application\whale.exe",
             $@"{ProgramFiles}\Naver\Naver Whale\Application\whale.exe"],
            ["whale"],
            ["Whale"]),
    ];

    public static BrowserAppDefinition? Find(string id) =>
        Definitions.FirstOrDefault(definition => definition.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<string> ExpandPaths(IEnumerable<string> candidates)
    {
        var results = new List<string>();
        foreach (var candidate in candidates)
        {
            var expanded = Environment.ExpandEnvironmentVariables(candidate);
            if (!expanded.Contains('*'))
            {
                results.Add(expanded);
                continue;
            }

            // Expand wildcards in any path segment, e.g. Packages\Arc_*\LocalCache.
            var current = new List<string> { Path.GetPathRoot(expanded) ?? string.Empty };
            var segments = expanded[(Path.GetPathRoot(expanded)?.Length ?? 0)..]
                .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            foreach (var segment in segments)
            {
                var next = new List<string>();
                foreach (var root in current)
                {
                    if (!segment.Contains('*'))
                    {
                        next.Add(Path.Combine(root, segment));
                        continue;
                    }

                    if (Directory.Exists(root))
                    {
                        try
                        {
                            next.AddRange(Directory.EnumerateDirectories(root, segment));
                        }
                        catch
                        {
                            // Ignore inaccessible matches.
                        }
                    }
                }

                current = next;
            }

            results.AddRange(current);
        }

        return results.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static BrowserAppDefinition CreateCustomDefinition(
        string id, string displayName, BrowserFamily family = BrowserFamily.Chromium) =>
        new(id, displayName, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<string>(), IsBuiltIn: false, Family: family);
}
