// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Install;

/// <summary>One known dependency from the embedded catalog.</summary>
public sealed record KnownDependency(
    string Id,
    string Label,
    string PackageId,
    IReadOnlyList<string> NeededByPackageIds,
    IReadOnlyList<string> NeededByNameWords);

/// <summary>
/// Loads the dependency catalog from an embedded JSON file, so the knowledge is data rather than
/// code and can be extended without a rebuild of anything but this assembly.
/// </summary>
public sealed class DependencyCatalog
{
    private const string ResourceName = "CtrlZ.Core.Install.Dependencies.dependency-catalog.json";

    private DependencyCatalog(IReadOnlyList<KnownDependency> dependencies)
    {
        Dependencies = dependencies;
    }

    public IReadOnlyList<KnownDependency> Dependencies { get; }

    public static DependencyCatalog Empty { get; } = new(Array.Empty<KnownDependency>());

    public static DependencyCatalog LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return Empty;
        }

        using var reader = new StreamReader(stream);
        return FromJson(reader.ReadToEnd());
    }

    public static DependencyCatalog FromJson(string json)
    {
        try
        {
            var document = JsonSerializer.Deserialize<CatalogDocument>(json, JsonOptions);
            if (document?.Dependencies is null)
            {
                return Empty;
            }

            var mapped = document.Dependencies
                .Select(record => new KnownDependency(
                    record.Id,
                    record.Label,
                    record.PackageId,
                    record.NeededBy?.PackageIds ?? new List<string>(),
                    record.NeededBy?.NameWords ?? new List<string>()))
                .ToList();

            return new DependencyCatalog(mapped);
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    /// <summary>The dependencies a saved app needs, matched by winget id first and then by whole words.</summary>
    public IReadOnlyList<KnownDependency> For(AppEntry app)
    {
        var matches = new List<KnownDependency>();
        var packageId = app.PackageId?.Trim() ?? string.Empty;
        var name = app.Name?.Trim() ?? string.Empty;

        foreach (var dependency in Dependencies)
        {
            var byId = dependency.NeededByPackageIds.Any(id =>
                string.Equals(id, packageId, StringComparison.OrdinalIgnoreCase));

            var byName = dependency.NeededByNameWords.Any(word =>
                word.Length > 0 && name.Contains(word, StringComparison.OrdinalIgnoreCase));

            if (byId || byName)
            {
                matches.Add(dependency);
            }
        }

        return matches;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private sealed class CatalogDocument
    {
        [JsonPropertyName("dependencies")]
        public List<KnownDependencyRecord>? Dependencies { get; set; }
    }

    private sealed class KnownDependencyRecord
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;
        [JsonPropertyName("packageId")] public string PackageId { get; set; } = string.Empty;
        [JsonPropertyName("neededBy")] public NeededByRecord? NeededBy { get; set; }
    }

    private sealed class NeededByRecord
    {
        [JsonPropertyName("packageIds")] public List<string>? PackageIds { get; set; }
        [JsonPropertyName("nameWords")] public List<string>? NameWords { get; set; }
    }
}
