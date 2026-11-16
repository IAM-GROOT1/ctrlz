// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Text.RegularExpressions;
using Microsoft.Win32;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Services;

/// <summary>Reads Add/Remove Programs entries from all three registry locations.</summary>
public sealed class RegistryAppScanner
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private readonly IAppLog _log;

    public RegistryAppScanner(IAppLog log)
    {
        _log = log;
    }

    public IReadOnlyList<ScannedApp> Scan()
    {
        var results = new List<ScannedApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (hive, view) in Locations())
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(UninstallKey);
                if (uninstall is null)
                {
                    continue;
                }

                foreach (var subKeyName in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using var subKey = uninstall.OpenSubKey(subKeyName);
                        var app = ReadEntry(subKey, hive, view, subKeyName);
                        if (app is null)
                        {
                            continue;
                        }

                        var key = $"{app.Name.ToLowerInvariant()}|{app.Version?.ToLowerInvariant()}|{app.Publisher?.ToLowerInvariant()}";
                        if (seen.Add(key))
                        {
                            results.Add(app);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Write(LogLevel.Debug, $"Could not read uninstall entry {hive}/{view}/{subKeyName}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Write(LogLevel.Warning, $"Could not open uninstall registry location {hive}/{view}.", ex);
            }
        }

        _log.Write(LogLevel.Info, $"Registry scan found {results.Count} programs.");
        return results;
    }

    private static ScannedApp? ReadEntry(RegistryKey? key, RegistryHive hive, RegistryView view, string subKeyName)
    {
        if (key is null)
        {
            return null;
        }

        var name = GetString(key, "DisplayName");
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('@'))
        {
            return null;
        }

        if (GetInt(key, "SystemComponent") == 1 || GetInt(key, "NoDisplay") == 1)
        {
            return null;
        }

        var parent = GetString(key, "ParentKeyName");
        if (!string.IsNullOrWhiteSpace(parent))
        {
            return null;
        }

        var releaseType = GetString(key, "ReleaseType") ?? string.Empty;
        if (releaseType.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
            releaseType.Contains("Hotfix", StringComparison.OrdinalIgnoreCase) ||
            releaseType.Contains("Security Update", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (Regex.IsMatch(name, @"^(KB\d{6,}|Update for |Security Update for |Hotfix for )",
                RegexOptions.IgnoreCase))
        {
            return null;
        }

        var publisher = GetString(key, "Publisher");
        var version = GetString(key, "DisplayVersion");
        var installLocation = NormalizeDirectory(GetString(key, "InstallLocation"));
        var icon = NormalizeIcon(GetString(key, "DisplayIcon"));
        if (icon is null && installLocation is not null)
        {
            icon = FindLikelyExecutable(installLocation, name);
        }

        return new ScannedApp
        {
            Name = name.Trim(),
            Publisher = string.IsNullOrWhiteSpace(publisher) ? null : publisher.Trim(),
            Version = string.IsNullOrWhiteSpace(version) ? null : version.Trim(),
            Source = AppSource.Winget,
            Category = AppClassifier.Classify(name, publisher),
            InstallLocation = installLocation,
            UninstallString = GetString(key, "UninstallString"),
            IconPath = icon,
            RegistryKey = $"{hive}\\{view}\\{UninstallKey}\\{subKeyName}"
        };
    }

    private static IEnumerable<(RegistryHive Hive, RegistryView View)> Locations()
    {
        yield return (RegistryHive.LocalMachine, RegistryView.Registry64);
        yield return (RegistryHive.LocalMachine, RegistryView.Registry32);
        if (Environment.Is64BitOperatingSystem)
        {
            // On a 64-bit OS, HKCU\Software\WOW6432Node is not covered by Registry32 for HKCU
            // on all Windows versions, so also read the explicit path.
            yield return (RegistryHive.CurrentUser, RegistryView.Registry64);
            yield return (RegistryHive.CurrentUser, RegistryView.Registry32);
        }
        else
        {
            yield return (RegistryHive.CurrentUser, RegistryView.Registry32);
        }
    }

    private static string? GetString(RegistryKey key, string name)
    {
        try { return key.GetValue(name) as string; }
        catch { return null; }
    }

    private static int GetInt(RegistryKey key, string name)
    {
        try
        {
            var value = key.GetValue(name);
            return value switch
            {
                int i => i,
                long l => (int)l,
                string s when int.TryParse(s, out var parsed) => parsed,
                _ => 0
            };
        }
        catch
        {
            return 0;
        }
    }

    private static string? NormalizeDirectory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
            return Directory.Exists(expanded) ? expanded : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeIcon(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var path = value.Trim();
        if (path.StartsWith('"'))
        {
            var closing = path.IndexOf('"', 1);
            path = closing > 1 ? path[1..closing] : path.Trim('"');
        }
        else
        {
            path = Regex.Replace(path, @",\s*-?\d+\s*$", string.Empty).Trim();
        }

        try
        {
            path = Environment.ExpandEnvironmentVariables(path);
            if (!File.Exists(path))
            {
                return null;
            }

            var extension = Path.GetExtension(path);
            return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".ico", StringComparison.OrdinalIgnoreCase)
                ? path
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindLikelyExecutable(string directory, string appName)
    {
        try
        {
            var candidates = Directory.EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly)
                .Where(path =>
                {
                    var file = Path.GetFileNameWithoutExtension(path);
                    return !file.Contains("uninstall", StringComparison.OrdinalIgnoreCase) &&
                           !file.Contains("update", StringComparison.OrdinalIgnoreCase) &&
                           !file.Contains("setup", StringComparison.OrdinalIgnoreCase) &&
                           !file.Contains("helper", StringComparison.OrdinalIgnoreCase);
                })
                .Take(200)
                .ToArray();

            if (candidates.Length == 0)
            {
                return null;
            }

            var normalizedApp = WingetMatcher.NormalizeName(appName);
            return candidates
                .OrderByDescending(path =>
                {
                    var file = WingetMatcher.NormalizeName(Path.GetFileNameWithoutExtension(path));
                    return file.Length > 2 && normalizedApp.Contains(file, StringComparison.Ordinal) ? file.Length : 0;
                })
                .ThenBy(path => path.Length)
                .First();
        }
        catch
        {
            return null;
        }
    }
}
