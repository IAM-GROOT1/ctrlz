// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CtrlZ.App.Localization;
using Xunit;

namespace CtrlZ.App.Tests;

/// <summary>
/// Keeps the app's text and its resource files in step: every key the app uses exists in English,
/// no English key is left unused, no new literal text slips into XAML, and every translation uses
/// the same keys and the same {0}-style placeholders as English.
/// </summary>
public class LocalizationTests
{
    private static readonly Regex XamlKey = new(@"\{l:Tr (?<key>[A-Za-z0-9_]+)\}");
    private static readonly Regex CodeKey = new(@"\bLoc\.(?:Get|Format)\(\s*""(?<key>[A-Za-z0-9_]+)""");
    private static readonly Regex Placeholder = new(@"\{(\d+)(?:[,:][^}]*)?\}");

    private static readonly Regex LiteralTextAttribute = new(
        @"(?<=\s)(?:Text|Content|Header|ToolTip|Title|AutomationProperties\.Name|AutomationProperties\.HelpText)=""(?<value>[^""{][^""]*|\{\}[^""]*)""");

    private static string AppFolder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "CtrlZ.App")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new DirectoryNotFoundException("Could not find src/CtrlZ.App above the test output.")
            : Path.Combine(directory.FullName, "src", "CtrlZ.App");
    }

    private static IEnumerable<string> SourceFiles(string pattern) =>
        Directory.EnumerateFiles(AppFolder(), pattern, SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                           !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));

    private static Dictionary<string, string> ReadResx(string path) =>
        XDocument.Load(path).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? string.Empty, StringComparer.Ordinal);

    private static string EnglishPath() => Path.Combine(AppFolder(), "Resources", "Strings.resx");

    private static HashSet<string> UsedKeys()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in SourceFiles("*.xaml"))
        {
            foreach (Match match in XamlKey.Matches(File.ReadAllText(file)))
            {
                used.Add(match.Groups["key"].Value);
            }
        }

        foreach (var file in SourceFiles("*.cs"))
        {
            foreach (Match match in CodeKey.Matches(File.ReadAllText(file)))
            {
                used.Add(match.Groups["key"].Value);
            }
        }

        return used;
    }

    [Fact]
    public void Every_key_the_app_uses_has_English_text()
    {
        var english = ReadResx(EnglishPath());
        var missing = UsedKeys().Where(key => !english.ContainsKey(key)).OrderBy(k => k).ToList();
        Assert.True(missing.Count == 0, "Keys used but missing from Strings.resx:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void No_English_text_is_left_unused()
    {
        var used = UsedKeys();
        var unused = ReadResx(EnglishPath()).Keys.Where(key => !used.Contains(key)).OrderBy(k => k).ToList();
        Assert.True(unused.Count == 0, "Keys in Strings.resx that nothing uses:\n" + string.Join("\n", unused));
    }

    [Fact]
    public void Keys_differ_by_more_than_letter_case()
    {
        // .resx names are case-insensitive when compiled, so "Notes" and "NOTES" would collide.
        var duplicates = XDocument.Load(EnglishPath()).Root!.Elements("data")
            .GroupBy(d => (string)d.Attribute("name")!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        Assert.Empty(duplicates);
    }

    [Fact]
    public void No_literal_text_is_written_straight_into_XAML()
    {
        var literals = new List<string>();
        foreach (var file in SourceFiles("*.xaml").Where(p => !p.Contains(Path.DirectorySeparatorChar + "Themes" + Path.DirectorySeparatorChar)))
        {
            foreach (Match match in LiteralTextAttribute.Matches(File.ReadAllText(file)))
            {
                var value = XElement.Parse($"<x a=\"{match.Groups["value"].Value}\"/>").Attribute("a")!.Value;
                // Icons, separators and the product name are not translated.
                if (value == "CTRL+Z" || !Regex.IsMatch(value, "[A-Za-z]{2,}"))
                {
                    continue;
                }

                literals.Add($"{Path.GetFileName(file)}: {match.Value}");
            }
        }

        Assert.True(literals.Count == 0, "Use {l:Tr Key} for this text:\n" + string.Join("\n", literals));
    }

    [Fact]
    public void Translations_use_the_same_keys_and_placeholders_as_English()
    {
        var english = ReadResx(EnglishPath());
        var problems = new List<string>();
        foreach (var path in Directory.EnumerateFiles(Path.GetDirectoryName(EnglishPath())!, "Strings.*.resx"))
        {
            var language = Path.GetFileNameWithoutExtension(path)["Strings.".Length..];
            _ = CultureInfo.GetCultureInfo(language); // throws for a file name that is not a language
            foreach (var (key, value) in ReadResx(path))
            {
                if (!english.TryGetValue(key, out var source))
                {
                    problems.Add($"{language}: {key} is not an English key");
                    continue;
                }

                if (!Placeholders(source).SetEquals(Placeholders(value)))
                {
                    problems.Add($"{language}: {key} has different placeholders from English");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void English_text_is_embedded_and_read_back()
    {
        Assert.Equal("Apply or undo: Dark mode", Loc.Format("Tweaks_ApplyOrUndoName", "Dark mode"));
        Assert.Equal("[No_such_key]", Loc.Get("No_such_key"));
    }

    private static HashSet<string> Placeholders(string text) =>
        Placeholder.Matches(text).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
}
