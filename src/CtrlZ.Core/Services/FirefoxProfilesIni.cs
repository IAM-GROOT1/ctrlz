// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Text;
using CtrlZ.Core.Infrastructure;

namespace CtrlZ.Core.Services;

public sealed class FirefoxIniSection
{
    public FirefoxIniSection(string name)
    {
        Name = name;
    }

    public string Name { get; }
    public List<KeyValuePair<string, string>> Entries { get; } = new();

    public string? Get(string key) =>
        Entries.FirstOrDefault(pair => pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

    public void Set(string key, string value)
    {
        var index = Entries.FindIndex(pair => pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            Entries[index] = new KeyValuePair<string, string>(key, value);
        }
        else
        {
            Entries.Add(new KeyValuePair<string, string>(key, value));
        }
    }
}

/// <summary>
/// Minimal, lossless-enough profiles.ini reader/writer for Firefox-family browsers.
/// It preserves all sections and keys; comments/blank lines are not important and are
/// discarded when CTRL+Z adds or renames a profile.
/// </summary>
public sealed class FirefoxProfilesIni
{
    private readonly List<FirefoxIniSection> _sections = new();

    public IReadOnlyList<FirefoxIniSection> Sections => _sections;
    public string? FilePath { get; private set; }

    public static FirefoxProfilesIni Load(string profileRoot)
    {
        var ini = new FirefoxProfilesIni();
        var path = Path.Combine(profileRoot, "profiles.ini");
        ini.FilePath = path;
        if (!File.Exists(path))
        {
            return ini;
        }

        FirefoxIniSection? current = null;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.TrimStart('\uFEFF').Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = new FirefoxIniSection(line[1..^1].Trim());
                ini._sections.Add(current);
                continue;
            }

            if (current is null)
            {
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            current.Set(line[..equals].Trim(), line[(equals + 1)..].Trim());
        }

        return ini;
    }

    public IEnumerable<FirefoxIniSection> ProfileSections =>
        _sections.Where(section => section.Name.StartsWith("Profile", StringComparison.OrdinalIgnoreCase));

    public string? FindInstallDefault()
    {
        foreach (var section in _sections.Where(section =>
                     section.Name.StartsWith("Install", StringComparison.OrdinalIgnoreCase)))
        {
            var value = section.Get("Default");
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    public bool HasDefaultFlag =>
        ProfileSections.Any(section => section.Get("Default") == "1");

    /// <summary>
    /// Adds a profile section, or updates the Name for an existing matching path. Returns
    /// the section key (ProfileN) that was created or updated.
    /// </summary>
    public string AddOrUpdate(string displayName, string relativePath)
    {
        var normalized = NormalizePath(relativePath);
        foreach (var section in ProfileSections)
        {
            var existing = NormalizePath(section.Get("Path") ?? string.Empty);
            if (existing.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                section.Set("Name", displayName);
                section.Set("Path", normalized);
                section.Set("IsRelative", "1");
                return section.Name;
            }
        }

        var next = 0;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in ProfileSections)
        {
            used.Add(section.Name);
            if (section.Name.StartsWith("Profile", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(section.Name["Profile".Length..], out var index))
            {
                next = Math.Max(next, index + 1);
            }
        }

        while (used.Contains($"Profile{next}"))
        {
            next++;
        }

        var newSection = new FirefoxIniSection($"Profile{next}");
        newSection.Set("Name", displayName);
        newSection.Set("IsRelative", "1");
        newSection.Set("Path", normalized);
        _sections.Add(newSection);
        return newSection.Name;
    }

    public void Save()
    {
        if (string.IsNullOrWhiteSpace(FilePath))
        {
            throw new InvalidOperationException("profiles.ini path is not set.");
        }

        var builder = new StringBuilder();
        foreach (var section in _sections)
        {
            builder.Append('[').Append(section.Name).Append(']').AppendLine();
            foreach (var pair in section.Entries)
            {
                builder.Append(pair.Key).Append('=').AppendLine(pair.Value);
            }

            builder.AppendLine();
        }

        FileUtil.AtomicWriteAllText(FilePath, builder.ToString());
    }

    public static string NormalizePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');
}
