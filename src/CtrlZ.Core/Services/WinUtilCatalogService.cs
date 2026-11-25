// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Globalization;
using System.Reflection;
using System.Text.Json;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Localization;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Services;

public sealed record WinUtilCatalogItem(
    string Key,
    string Name,
    string Category,
    string? WingetId,
    string? ChocoId,
    string? Description,
    string? Link,
    bool IsFoss)
{
    public AppCategory CategoryEnum => CategoryNames.ParseCategory(Category);
    public bool CanInstall => !string.IsNullOrWhiteSpace(WingetId);
    public string CategoryLabel => string.IsNullOrWhiteSpace(Category) ? "Other" : Category;
}

public sealed record WinUtilCatalog(
    IReadOnlyList<WinUtilCatalogItem> Items,
    DateTimeOffset? FetchedUtc,
    bool FromCache,
    bool IsBundledFallback,
    string? Warning = null)
{
    /// <summary>Where the list came from, in English, for the log.</summary>
    public string StatusText => IsBundledFallback
        ? "Bundled snapshot"
        : FromCache
            ? $"Cached {(FetchedUtc is null ? string.Empty : FetchedUtc.Value.ToLocalTime().ToString("MMM d, HH:mm", CultureInfo.InvariantCulture))}".Trim()
            : $"Live · {FetchedUtc?.ToLocalTime().ToString("MMM d, HH:mm", CultureInfo.InvariantCulture)}";

    /// <summary>The same, in the user's language, for the Catalog page.</summary>
    public string DisplayStatus => IsBundledFallback
        ? CoreText.Get("Catalog_StatusBundled")
        : FromCache
            ? FetchedUtc is null
                ? CoreText.Get("Catalog_StatusCachedUndated")
                : CoreText.Format("Catalog_StatusCached", When(FetchedUtc.Value))
            : CoreText.Format("Catalog_StatusLive", FetchedUtc is null ? string.Empty : When(FetchedUtc.Value));

    /// <summary>A date and time in the user's format, kept in order inside right-to-left text.</summary>
    private static string When(DateTimeOffset utc) =>
        CoreText.LeftToRight(utc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
}

/// <summary>
/// Chris Titus Tech's WinUtil catalog. The raw JSON is cached locally and a build-time
/// snapshot is embedded, so the Catalog page is useful even before the first network call.
/// </summary>
public sealed class WinUtilCatalogService
{
    private const string LiveUrl = "https://raw.githubusercontent.com/ChrisTitusTech/winutil/main/config/applications.json";
    private const string AlternateUrl = "https://github.com/ChrisTitusTech/winutil/raw/main/config/applications.json";
    private const string ResourceName = "CtrlZ.Core.Resources.winutil-applications.json";
    private static readonly TimeSpan CacheMaxAge = TimeSpan.FromHours(24);

    private readonly AppPaths _paths;
    private readonly DownloadService _downloads;
    private readonly IAppLog _log;

    public WinUtilCatalogService(AppPaths paths, DownloadService downloads, IAppLog log)
    {
        _paths = paths;
        _downloads = downloads;
        _log = log;
    }

    /// <summary>
    /// The catalog. With <paramref name="autoRefresh"/> off (Settings), nothing is downloaded unless
    /// <paramref name="forceRefresh"/> is set by the Refresh button: the copy saved on this PC is used
    /// at any age, or the snapshot built into CTRL+Z when there is none.
    /// </summary>
    public async Task<WinUtilCatalog> LoadAsync(bool forceRefresh, bool autoRefresh = true, CancellationToken cancellationToken = default)
    {
        var cached = TryLoadCache();
        if (!forceRefresh && !autoRefresh)
        {
            return cached ?? LoadBundled();
        }

        if (cached is not null && !forceRefresh && cached.FetchedUtc is not null &&
            DateTimeOffset.UtcNow - cached.FetchedUtc.Value < CacheMaxAge)
        {
            return cached;
        }

        try
        {
            var json = await DownloadCatalogJsonAsync(cancellationToken).ConfigureAwait(false);
            var items = Parse(json);
            if (items.Count == 0)
            {
                throw new InvalidDataException("The live WinUtil catalog was empty.");
            }

            var fetched = DateTimeOffset.UtcNow;
            FileUtil.AtomicWriteAllText(_paths.CatalogCacheFile, json);
            FileUtil.AtomicWriteAllText(_paths.CatalogMetadataFile,
                JsonSerializer.Serialize(new { fetchedUtc = fetched, source = LiveUrl }, JsonDefaults.Profile));
            _log.Write(LogLevel.Info, $"Refreshed WinUtil catalog: {items.Count} apps.");
            return new WinUtilCatalog(items, fetched, FromCache: false, IsBundledFallback: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Write(LogLevel.Warning, "Could not refresh the WinUtil catalog; using a cached copy.", ex);
            if (cached is not null)
            {
                return cached with
                {
                    Warning = CoreText.Get("Catalog_OfflineCached")
                };
            }

            var bundled = LoadBundled();
            return bundled with
            {
                Warning = CoreText.Get("Catalog_OfflineBundled")
            };
        }
    }

    public WinUtilCatalog? TryLoadCache()
    {
        try
        {
            if (!File.Exists(_paths.CatalogCacheFile))
            {
                return null;
            }

            var items = Parse(File.ReadAllText(_paths.CatalogCacheFile));
            DateTimeOffset? fetched = null;
            if (File.Exists(_paths.CatalogMetadataFile))
            {
                using var metadata = JsonDocument.Parse(File.ReadAllText(_paths.CatalogMetadataFile));
                if (metadata.RootElement.TryGetProperty("fetchedUtc", out var value) &&
                    value.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(value.GetString(), out var parsed))
                {
                    fetched = parsed;
                }
            }

            return new WinUtilCatalog(items, fetched, FromCache: true, IsBundledFallback: false);
        }
        catch (Exception ex)
        {
            _log.Write(LogLevel.Warning, "Cached WinUtil catalog could not be read.", ex);
            return null;
        }
    }

    public WinUtilCatalog LoadBundled()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                return new WinUtilCatalog(Array.Empty<WinUtilCatalogItem>(), null, true, true,
                    "The bundled catalog is missing from this build.");
            }

            using var reader = new StreamReader(stream);
            return new WinUtilCatalog(Parse(reader.ReadToEnd()), null, true, true);
        }
        catch (Exception ex)
        {
            _log.Write(LogLevel.Error, "Bundled WinUtil catalog could not be read.", ex);
            return new WinUtilCatalog(Array.Empty<WinUtilCatalogItem>(), null, true, true,
                "The bundled catalog could not be read.");
        }
    }

    public static IReadOnlyList<WinUtilCatalogItem> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var items = new List<WinUtilCatalogItem>();
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return items;
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var entry = property.Value;
            var name = GetString(entry, "content");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var winget = GetString(entry, "winget");
            var category = GetString(entry, "category") ?? "Other";
            var description = GetString(entry, "description");
            var link = GetString(entry, "link");
            var choco = GetString(entry, "choco");
            var foss = entry.TryGetProperty("foss", out var fossElement) &&
                       fossElement.ValueKind == JsonValueKind.True;

            items.Add(new WinUtilCatalogItem(
                property.Name,
                name.Trim(),
                category.Trim(),
                string.IsNullOrWhiteSpace(winget) ? null : winget.Trim(),
                string.IsNullOrWhiteSpace(choco) ? null : choco.Trim(),
                description,
                link,
                foss));
        }

        return items
            .GroupBy(item => item.WingetId ?? item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.CategoryLabel, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private async Task<string> DownloadCatalogJsonAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _downloads.GetStringAsync(LiveUrl, TimeSpan.FromSeconds(30), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return await _downloads.GetStringAsync(AlternateUrl, TimeSpan.FromSeconds(30), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
