// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CtrlZ.App.Localization;
using CtrlZ.Core.Localization;
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
    private static readonly Regex AppCodeKey = new(@"\bLoc\.(?<kind>Get|Format|Plural)\(\s*""(?<key>[A-Za-z0-9_]+)""");
    private static readonly Regex CoreCodeKey = new(@"\bCoreText\.(?<kind>Get|Format|Plural)\(\s*""(?<key>[A-Za-z0-9_]+)""");
    private static readonly Regex Placeholder = new(@"\{(\d+)(?:[,:][^}]*)?\}");

    private static readonly Regex LiteralTextAttribute = new(
        @"(?<=\s)(?:Text|Content|Header|ToolTip|Title|AutomationProperties\.Name|AutomationProperties\.HelpText)=""(?<value>[^""{][^""]*|\{\}[^""]*)""");

    /// <summary>The two resource sets: the app's screens ("app") and the core library ("core").</summary>
    private sealed record TextSet(string Project, string ResourceName, Regex CodeKey);

    private static TextSet Set(string name) => name == "core"
        ? new TextSet("CtrlZ.Core", "CoreStrings", CoreCodeKey)
        : new TextSet("CtrlZ.App", "Strings", AppCodeKey);

    private static string SrcFolder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "CtrlZ.App")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new DirectoryNotFoundException("Could not find src/CtrlZ.App above the test output.")
            : Path.Combine(directory.FullName, "src");
    }

    private static IEnumerable<string> SourceFiles(string pattern, string project = "CtrlZ.App") =>
        Directory.EnumerateFiles(Path.Combine(SrcFolder(), project), pattern, SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                           !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));

    private static Dictionary<string, string> ReadResx(string path) =>
        XDocument.Load(path).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? string.Empty, StringComparer.Ordinal);

    private static string EnglishPath(TextSet set) =>
        Path.Combine(SrcFolder(), set.Project, "Resources", set.ResourceName + ".resx");

    /// <summary>Plural forms a translation may add beyond English's One and Other.</summary>
    private static readonly HashSet<string> PluralExtras = new(StringComparer.Ordinal);

    private static HashSet<string> UsedKeys(TextSet set)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        if (set.Project == "CtrlZ.App")
        {
            foreach (var file in SourceFiles("*.xaml"))
            {
                foreach (Match match in XamlKey.Matches(File.ReadAllText(file)))
                {
                    used.Add(match.Groups["key"].Value);
                }
            }
        }

        // Core text can also be read by the app and the command line, so scan every project.
        var projects = set.Project == "CtrlZ.App" ? ["CtrlZ.App"] : new[] { "CtrlZ.Core", "CtrlZ.App", "CtrlZ.Cli" };
        foreach (var file in projects.SelectMany(project => SourceFiles("*.cs", project)))
        {
            foreach (Match match in set.CodeKey.Matches(File.ReadAllText(file)))
            {
                var key = match.Groups["key"].Value;
                if (match.Groups["kind"].Value == "Plural")
                {
                    // English needs both forms; other languages may add Zero, Two, Few or Many.
                    used.Add(key + "_One");
                    used.Add(key + "_Other");
                    foreach (var form in new[] { "_Zero", "_Two", "_Few", "_Many" })
                    {
                        PluralExtras.Add(key + form);
                    }
                }
                else
                {
                    used.Add(key);
                }
            }
        }

        return used;
    }

    [Theory]
    [InlineData("app")]
    [InlineData("core")]
    public void Every_key_the_code_uses_has_English_text(string setName)
    {
        var set = Set(setName);
        var english = ReadResx(EnglishPath(set));
        var missing = UsedKeys(set).Where(key => !english.ContainsKey(key)).OrderBy(k => k).ToList();
        Assert.True(missing.Count == 0, $"Keys used but missing from {set.ResourceName}.resx:\n" + string.Join("\n", missing));
    }

    [Theory]
    [InlineData("app")]
    [InlineData("core")]
    public void No_English_text_is_left_unused(string setName)
    {
        var set = Set(setName);
        var used = UsedKeys(set);
        var unused = ReadResx(EnglishPath(set)).Keys.Where(key => !used.Contains(key)).OrderBy(k => k).ToList();
        Assert.True(unused.Count == 0, $"Keys in {set.ResourceName}.resx that nothing uses:\n" + string.Join("\n", unused));
    }

    [Theory]
    [InlineData("app")]
    [InlineData("core")]
    public void Keys_differ_by_more_than_letter_case(string setName)
    {
        // .resx names are case-insensitive when compiled, so "Notes" and "NOTES" would collide.
        var duplicates = XDocument.Load(EnglishPath(Set(setName))).Root!.Elements("data")
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

    [Theory]
    [InlineData("app")]
    [InlineData("core")]
    public void Translations_use_the_same_keys_and_placeholders_as_English(string setName)
    {
        var set = Set(setName);
        var english = ReadResx(EnglishPath(set));
        var problems = new List<string>();
        foreach (var path in Directory.EnumerateFiles(Path.GetDirectoryName(EnglishPath(set))!, set.ResourceName + ".*.resx"))
        {
            var language = Path.GetFileNameWithoutExtension(path)[(set.ResourceName.Length + 1)..];
            _ = CultureInfo.GetCultureInfo(language); // throws for a file name that is not a language
            foreach (var (key, value) in ReadResx(path))
            {
                if (!english.TryGetValue(key, out var source))
                {
                    _ = UsedKeys(set);
                    var pluralBase = key[..Math.Max(key.LastIndexOf('_'), 0)];
                    if (PluralExtras.Contains(key) && english.TryGetValue(pluralBase + "_Other", out var other))
                    {
                        source = other;
                    }
                    else
                    {
                        problems.Add($"{language}: {key} is not an English key");
                        continue;
                    }
                }

                try
                {
                    _ = string.Format(CultureInfo.InvariantCulture, value, 1, "x", "y", "z", DateTime.Now, 0.5);
                }
                catch (FormatException)
                {
                    problems.Add($"{language}: {key} is not a valid format string");
                }

                // Many values are joined to the next one in code, so the edges must match English.
                if (char.IsWhiteSpace(source.FirstOrDefault()) != char.IsWhiteSpace(value.FirstOrDefault()) ||
                    char.IsWhiteSpace(source.LastOrDefault()) != char.IsWhiteSpace(value.LastOrDefault()))
                {
                    problems.Add($"{language}: {key} starts or ends with different spacing from English");
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
        Assert.Equal("1 app", Loc.Plural("MyApps_AppCount", 1));
        Assert.Equal("3 apps", Loc.Plural("MyApps_AppCount", 3));
        Assert.Equal("Added 2 apps to \"Home\"", Loc.Plural("Catalog_AddedApps", 2, "Home"));
        Assert.Equal("Age: 1 day", CoreText.Plural("ReadinessScanner_AgeDays", 1));
    }

    [Theory]
    [InlineData("en", 1, "One")]
    [InlineData("en", 0, "Other")]
    [InlineData("de", 2, "Other")]
    [InlineData("fr", 0, "One")]
    [InlineData("fr", 2, "Other")]
    [InlineData("ru", 21, "One")]
    [InlineData("ru", 3, "Few")]
    [InlineData("ru", 12, "Many")]
    [InlineData("pl", 22, "Few")]
    [InlineData("pl", 25, "Many")]
    [InlineData("ar", 0, "Zero")]
    [InlineData("ar", 2, "Two")]
    [InlineData("ar", 105, "Few")]
    [InlineData("ar", 11, "Many")]
    [InlineData("ar", 100, "Other")]
    [InlineData("ja", 1, "Other")]
    public void Plural_categories_follow_each_language(string language, long count, string expected)
    {
        Assert.Equal(expected, PluralRules.Category(CultureInfo.GetCultureInfo(language), count));
    }

    private static HashSet<string> Placeholders(string text) =>
        Placeholder.Matches(text).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
}
