// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Services;

public enum PackageListKind
{
    Winget,
    Scoop,
    Chocolatey
}

/// <summary>One package as the other manager listed it.</summary>
public sealed record ListedPackage(string Id, string? Source = null);

/// <summary>Apps ready to add, and the packages that had no winget or Store match.</summary>
public sealed record PackageListImport(PackageListKind Kind, IReadOnlyList<AppEntry> Apps, IReadOnlyList<string> Unmatched)
{
    public string KindName => Kind switch
    {
        PackageListKind.Winget => "winget",
        PackageListKind.Scoop => "Scoop",
        _ => "Chocolatey"
    };
}

/// <summary>
/// Reads an app list exported by another package manager and turns it into CTRL+Z apps:
/// <c>winget export</c> JSON, <c>scoop export</c> JSON (or its older text lines) and Chocolatey's
/// <c>packages.config</c>. Everything becomes a winget or Microsoft Store entry, since those are the
/// sources CTRL+Z installs from. Scoop and Chocolatey names are matched through the app catalog, and
/// anything without a match is reported, never guessed. Imported apps never carry a post-install
/// command, a note or a download link: only a package id and a name.
/// </summary>
public static partial class PackageListImporter
{
    public const long MaxFileBytes = 4 * 1024 * 1024;
    public const int MaxPackages = 2000;

    public static PackageListImport FromFile(string path, IReadOnlyList<WinUtilCatalogItem> catalog)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("The list file was not found.", path);
        }

        if (info.Length > MaxFileBytes)
        {
            throw new FormatException("That file is too large to be an exported app list.");
        }

        return FromText(File.ReadAllText(path), catalog);
    }

    public static PackageListImport FromText(string text, IReadOnlyList<WinUtilCatalogItem> catalog)
    {
        var (kind, packages) = Parse(text);
        return Resolve(kind, packages, catalog);
    }

    /// <summary>Works out which manager wrote the text and lists its packages.</summary>
    /// <exception cref="FormatException">The text is not a list this importer knows.</exception>
    public static (PackageListKind Kind, IReadOnlyList<ListedPackage> Packages) Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.TrimStart((char)0xFEFF, ' ', '\t', '\r', '\n');
        var result = trimmed.StartsWith('<') ? (PackageListKind.Chocolatey, ParseChocolatey(trimmed))
            : trimmed.StartsWith('{') ? ParseJson(trimmed)
            : (PackageListKind.Scoop, ParseScoopText(trimmed));

        if (result.Item2.Count > MaxPackages)
        {
            throw new FormatException($"That list has more than {MaxPackages} packages.");
        }

        return result;
    }

    /// <summary>Maps listed packages to apps. Duplicates are folded; unknown ones are reported.</summary>
    public static PackageListImport Resolve(
        PackageListKind kind,
        IReadOnlyList<ListedPackage> packages,
        IReadOnlyList<WinUtilCatalogItem> catalog)
    {
        var byWinget = Index(catalog, item => item.WingetId);
        var byChoco = Index(catalog, item => item.ChocoId);
        var byName = Index(catalog, item => Normalize(item.Name));
        var byWingetTail = Index(catalog, item => item.WingetId is { } id ? Normalize(id[(id.LastIndexOf('.') + 1)..]) : null);

        var apps = new List<AppEntry>();
        var unmatched = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages)
        {
            var entry = kind switch
            {
                PackageListKind.Winget => FromWinget(package, byWinget),
                PackageListKind.Chocolatey => FromCatalog(Find(byChoco, package.Id)),
                _ => FromCatalog(Find(byChoco, package.Id) ?? Find(byName, Normalize(package.Id)) ?? Find(byWingetTail, Normalize(package.Id)))
            };

            if (entry is null)
            {
                if (seen.Add("?" + package.Id))
                {
                    unmatched.Add(package.Id);
                }

                continue;
            }

            if (seen.Add(entry.Source + ":" + entry.PackageId))
            {
                apps.Add(entry);
            }
        }

        return new PackageListImport(kind, apps, unmatched);
    }

    private static AppEntry? FromWinget(ListedPackage package, IReadOnlyDictionary<string, WinUtilCatalogItem> byWinget)
    {
        var store = string.Equals(package.Source, "msstore", StringComparison.OrdinalIgnoreCase);
        if (!store && package.Source is not null && !string.Equals(package.Source, "winget", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (store ? !StoreId().IsMatch(package.Id) : !WingetId().IsMatch(package.Id))
        {
            return null;
        }

        var known = Find(byWinget, package.Id);
        return new AppEntry
        {
            Name = known?.Name ?? package.Id,
            Source = store ? AppSource.MicrosoftStore : AppSource.Winget,
            PackageId = package.Id,
            Category = known?.CategoryEnum ?? AppCategory.Other
        };
    }

    private static AppEntry? FromCatalog(WinUtilCatalogItem? item)
    {
        if (item?.WingetId is not { } id || !WingetId().IsMatch(id))
        {
            return null;
        }

        return new AppEntry { Name = item.Name, Source = AppSource.Winget, PackageId = id, Category = item.CategoryEnum };
    }

    private static (PackageListKind, IReadOnlyList<ListedPackage>) ParseJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 16 });
            var root = document.RootElement;
            if (Property(root, "Sources") is { ValueKind: JsonValueKind.Array } sources)
            {
                var packages = new List<ListedPackage>();
                foreach (var source in sources.EnumerateArray())
                {
                    var sourceName = Property(source, "SourceDetails") is { } details ? Text(Property(details, "Name")) : null;
                    if (Property(source, "Packages") is not { ValueKind: JsonValueKind.Array } list)
                    {
                        continue;
                    }

                    foreach (var package in list.EnumerateArray())
                    {
                        if (Clean(Text(Property(package, "PackageIdentifier"))) is { } id)
                        {
                            packages.Add(new ListedPackage(id, sourceName ?? "winget"));
                        }
                    }
                }

                return (PackageListKind.Winget, packages);
            }

            if (Property(root, "apps") is { ValueKind: JsonValueKind.Array } apps)
            {
                var packages = new List<ListedPackage>();
                foreach (var app in apps.EnumerateArray())
                {
                    if (Clean(Text(Property(app, "Name"))) is { } name)
                    {
                        packages.Add(new ListedPackage(name, Text(Property(app, "Source"))));
                    }
                }

                return (PackageListKind.Scoop, packages);
            }
        }
        catch (JsonException ex)
        {
            throw new FormatException("That file is not valid JSON: " + ex.Message, ex);
        }

        throw new FormatException("That JSON is not a winget or Scoop export.");
    }

    private static IReadOnlyList<ListedPackage> ParseChocolatey(string text)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxFileBytes,
            IgnoreComments = true
        };

        var packages = new List<ListedPackage>();
        var sawRoot = false;
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), settings);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (reader.Depth == 0)
                {
                    sawRoot = reader.LocalName == "packages";
                }
                else if (sawRoot && reader.Depth == 1 && reader.LocalName == "package" && Clean(reader.GetAttribute("id")) is { } id)
                {
                    packages.Add(new ListedPackage(id));
                }
            }
        }
        catch (XmlException ex)
        {
            throw new FormatException("That file is not a valid packages.config: " + ex.Message, ex);
        }

        return sawRoot ? packages : throw new FormatException("That XML is not a Chocolatey packages.config.");
    }

    /// <summary>Older <c>scoop export</c> output: one <c>name (v1.2.3) [bucket]</c> line per app.</summary>
    private static IReadOnlyList<ListedPackage> ParseScoopText(string text)
    {
        var packages = new List<ListedPackage>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var match = ScoopLine().Match(line);
            if (!match.Success)
            {
                throw new FormatException("That file is not a winget, Scoop or Chocolatey export.");
            }

            packages.Add(new ListedPackage(match.Groups["name"].Value, match.Groups["bucket"].Success ? match.Groups["bucket"].Value : null));
        }

        return packages.Count > 0 ? packages : throw new FormatException("That file is empty.");
    }

    // Exact case: both exporters write fixed names, and a CTRL+Z profile's "Apps" must not read as Scoop's "apps".
    private static JsonElement? Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;

    private static string? Text(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    /// <summary>An id is kept only when it is one plain token: no spaces, quotes or control characters.</summary>
    private static string? Clean(string? id)
    {
        var value = id?.Trim();
        return string.IsNullOrEmpty(value) || value.Length > 128 || !PlainToken().IsMatch(value) ? null : value;
    }

    private static Dictionary<string, WinUtilCatalogItem> Index(IEnumerable<WinUtilCatalogItem> catalog, Func<WinUtilCatalogItem, string?> key)
    {
        var index = new Dictionary<string, WinUtilCatalogItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in catalog)
        {
            if (string.IsNullOrWhiteSpace(item.WingetId) || key(item) is not { Length: > 0 } value)
            {
                continue;
            }

            index.TryAdd(value, item);
        }

        return index;
    }

    private static WinUtilCatalogItem? Find(IReadOnlyDictionary<string, WinUtilCatalogItem> index, string key) =>
        index.TryGetValue(key, out var item) ? item : null;

    private static string Normalize(string value) => NonAlphanumeric().Replace(value, string.Empty).ToLowerInvariant();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+\-]{0,127}$")]
    private static partial Regex WingetId();

    [GeneratedRegex("^[A-Za-z0-9]{12}$")]
    private static partial Regex StoreId();

    [GeneratedRegex(@"^[A-Za-z0-9._+\-@/]+$")]
    private static partial Regex PlainToken();

    [GeneratedRegex(@"^(?<name>[A-Za-z0-9][A-Za-z0-9._+\-]*)(?:\s+\(v?[^)]*\))?(?:\s+\[(?<bucket>[^\]]+)\])?(?:\s+\{[^}]*\})?$")]
    private static partial Regex ScoopLine();

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NonAlphanumeric();
}
