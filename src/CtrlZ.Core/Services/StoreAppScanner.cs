// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Text.Json;
using System.Xml.Linq;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Services;

/// <summary>
/// Reads Store apps through PowerShell's Appx cmdlets, then uses Get-StartApps for
/// friendly display names. No WinRT projection dependency is required.
/// </summary>
public sealed class StoreAppScanner
{
    private readonly ProcessRunner _runner;
    private readonly IAppLog _log;

    public StoreAppScanner(ProcessRunner runner, IAppLog log)
    {
        _runner = runner;
        _log = log;
    }

    public async Task<IReadOnlyList<ScannedApp>> ScanAsync(CancellationToken cancellationToken = default)
    {
        const string script = """
            $ErrorActionPreference = 'SilentlyContinue'
            $packages = @(Get-AppxPackage | Where-Object {
                -not $_.IsFramework -and $_.SignatureKind -eq 'Store' -and -not $_.NonRemovable
            } | Select-Object Name, PackageFullName, Version, PublisherDisplayName, InstallLocation, PackageFamilyName)
            $start = @(Get-StartApps | Select-Object Name, AppID)
            [pscustomobject]@{ packages = $packages; startApps = $start } | ConvertTo-Json -Depth 5 -Compress
            """;

        var result = await _runner.RunAsync(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script],
            new ProcessRunOptions { Timeout = TimeSpan.FromMinutes(3) },
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            _log.Write(LogLevel.Warning, $"Store app scan failed ({result.ExitCode}): {result.StandardError.Trim()}");
            return Array.Empty<ScannedApp>();
        }

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            if (!document.RootElement.TryGetProperty("packages", out var packages))
            {
                return Array.Empty<ScannedApp>();
            }

            var startNames = ReadStartNames(document.RootElement);
            var apps = new List<ScannedApp>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var elements = packages.ValueKind == JsonValueKind.Array
                ? packages.EnumerateArray().ToArray()
                : new[] { packages };

            foreach (var package in elements)
            {
                if (package.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var packageName = GetString(package, "Name");
                var family = GetString(package, "PackageFamilyName");
                if (string.IsNullOrWhiteSpace(packageName) || string.IsNullOrWhiteSpace(family) || IsFramework(packageName))
                {
                    continue;
                }

                var displayName = ResolveDisplayName(startNames, family) ?? PrettifyPackageName(packageName);
                if (string.IsNullOrWhiteSpace(displayName) || !seen.Add(displayName))
                {
                    continue;
                }

                var installLocation = GetString(package, "InstallLocation");
                apps.Add(new ScannedApp
                {
                    Name = displayName.Trim(),
                    Publisher = GetString(package, "PublisherDisplayName"),
                    Version = GetString(package, "Version"),
                    Source = AppSource.MicrosoftStore,
                    IsStoreApp = true,
                    Category = AppClassifier.Classify(displayName, GetString(package, "PublisherDisplayName")),
                    InstallLocation = installLocation,
                    IconPath = FindStoreIcon(installLocation),
                    RegistryKey = family
                });
            }

            _log.Write(LogLevel.Info, $"Store scan found {apps.Count} apps.");
            return apps;
        }
        catch (Exception ex)
        {
            _log.Write(LogLevel.Warning, "Could not parse the Microsoft Store app list.", ex);
            return Array.Empty<ScannedApp>();
        }
    }

    private static Dictionary<string, string> ReadStartNames(JsonElement root)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("startApps", out var startApps))
        {
            return result;
        }

        var items = startApps.ValueKind == JsonValueKind.Array
            ? startApps.EnumerateArray().ToArray()
            : new[] { startApps };
        foreach (var item in items)
        {
            var name = GetString(item, "Name");
            var appId = GetString(item, "AppID");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(appId))
            {
                continue;
            }

            var bang = appId.IndexOf('!');
            var family = bang > 0 ? appId[..bang] : appId;
            if (!result.ContainsKey(family))
            {
                result[family] = name;
            }
        }

        return result;
    }

    private static string? ResolveDisplayName(Dictionary<string, string> startNames, string family)
    {
        return startNames.TryGetValue(family, out var name) ? name : null;
    }

    private static string PrettifyPackageName(string packageName)
    {
        var name = packageName;
        var firstDot = name.IndexOf('.');
        if (firstDot >= 0 && firstDot < name.Length - 1)
        {
            name = name[(firstDot + 1)..];
        }

        name = name.Replace("Microsoft.", string.Empty, StringComparison.OrdinalIgnoreCase);
        var withSpaces = System.Text.RegularExpressions.Regex.Replace(name, "(?<!^)(?=[A-Z])", " ");
        return withSpaces.Trim();
    }

    private static bool IsFramework(string packageName)
    {
        string[] prefixes =
        [
            "Microsoft.VCLibs", "Microsoft.UI.Xaml", "Microsoft.NET.Native", "Microsoft.Services.Store",
            "Microsoft.WindowsAppRuntime", "Microsoft.DesktopAppInstaller", "Microsoft.WindowsStore",
            "Microsoft.StorePurchaseApp", "Microsoft.AAD.BrokerPlugin", "Microsoft.AccountsControl",
            "Microsoft.LockApp", "Microsoft.Windows.ShellExperienceHost", "Microsoft.Windows.StartMenuExperienceHost",
            "MicrosoftWindows.Client.", "Microsoft.CredDialogHost", "Microsoft.ECApp", "Microsoft.Winget.Source"
        ];
        return prefixes.Any(prefix => packageName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindStoreIcon(string? installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation) || !Directory.Exists(installLocation))
        {
            return null;
        }

        try
        {
            var manifestPath = Path.Combine(installLocation, "AppxManifest.xml");
            if (File.Exists(manifestPath))
            {
                var manifest = XDocument.Load(manifestPath);
                var logo = manifest.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "Logo")?.Value;
                if (!string.IsNullOrWhiteSpace(logo))
                {
                    var logoPath = Path.Combine(installLocation, logo.Replace('\\', Path.DirectorySeparatorChar));
                    if (File.Exists(logoPath))
                    {
                        return logoPath;
                    }

                    var directory = Path.GetDirectoryName(logoPath);
                    var baseName = Path.GetFileNameWithoutExtension(logoPath);
                    if (directory is not null && Directory.Exists(directory))
                    {
                        var match = Directory.EnumerateFiles(directory, baseName + "*.png")
                            .OrderByDescending(path => path.Contains("targetsize-48", StringComparison.OrdinalIgnoreCase))
                            .ThenByDescending(path => new FileInfo(path).Length)
                            .FirstOrDefault();
                        if (match is not null)
                        {
                            return match;
                        }
                    }
                }
            }

            var assets = Path.Combine(installLocation, "Assets");
            if (Directory.Exists(assets))
            {
                return Directory.EnumerateFiles(assets, "*.png", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(path => path.Contains("Square44x44Logo", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(path => new FileInfo(path).Length)
                    .FirstOrDefault();
            }
        }
        catch
        {
            // Missing/unreadable app assets are not fatal.
        }

        return null;
    }

    private static string? GetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
