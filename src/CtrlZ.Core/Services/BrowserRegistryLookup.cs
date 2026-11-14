using System.Text.RegularExpressions;
using CtrlZ.Core.Models;
using Microsoft.Win32;

namespace CtrlZ.Core.Services;

public sealed record RegistryBrowserInfo(string DisplayName, string? Version, string? ExecutablePath);

/// <summary>Lightweight registry inventory used only to enrich browser detection.</summary>
public static class BrowserRegistryLookup
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static IReadOnlyList<RegistryBrowserInfo> FindAll()
    {
        var results = new List<RegistryBrowserInfo>();
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
                        using var key = uninstall.OpenSubKey(subKeyName);
                        var name = key?.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
                        {
                            continue;
                        }

                        var version = key?.GetValue("DisplayVersion") as string;
                        var icon = key?.GetValue("DisplayIcon") as string;
                        var exe = NormalizeExecutable(icon);
                        results.Add(new RegistryBrowserInfo(name.Trim(), version?.Trim(), exe));
                    }
                    catch
                    {
                        // Individual registry entries are not important enough to fail detection.
                    }
                }
            }
            catch
            {
                // Missing hive/view.
            }
        }

        return results;
    }

    public static RegistryBrowserInfo? Match(IReadOnlyList<RegistryBrowserInfo> entries, BrowserAppDefinition definition)
    {
        return entries.FirstOrDefault(entry =>
            definition.RegistryNamePatterns.Any(pattern =>
                entry.DisplayName.Contains(pattern, StringComparison.OrdinalIgnoreCase)));
    }

    private static string? NormalizeExecutable(string? value)
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
            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<(RegistryHive Hive, RegistryView View)> Locations()
    {
        yield return (RegistryHive.LocalMachine, RegistryView.Registry64);
        yield return (RegistryHive.LocalMachine, RegistryView.Registry32);
        yield return (RegistryHive.CurrentUser, RegistryView.Registry64);
        yield return (RegistryHive.CurrentUser, RegistryView.Registry32);
    }
}
