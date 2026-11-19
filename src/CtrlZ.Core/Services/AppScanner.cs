// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Services;

public sealed record ScanProgress(string Step, int Index, int Total, string? Detail = null, bool IsComplete = false);

public sealed class ScanResult
{
    public List<ScannedApp> Apps { get; init; } = new();
    public int RegistryCount { get; init; }
    public int StoreCount { get; init; }
    public int WingetOnlyCount { get; init; }
    public DateTimeOffset ScannedUtc { get; init; } = DateTimeOffset.Now;
    public List<string> Warnings { get; init; } = new();
    public string? WingetVersion { get; init; }

    public int MatchedCount => Apps.Count(a => a.IsMatched);
    public int UnmatchedCount => Apps.Count(a => !a.IsMatched);
}

/// <summary>
/// Merges three discovery sources into one de-duplicated list:
/// winget's own inventory, the registry, and Store package inventory. Matching is done
/// once here so every UI surface sees the same package ids.
/// </summary>
public sealed class AppScanner
{
    private readonly WingetCli _winget;
    private readonly RegistryAppScanner _registry;
    private readonly StoreAppScanner _store;
    private readonly IAppLog _log;

    public AppScanner(WingetCli winget, RegistryAppScanner registry, StoreAppScanner store, IAppLog log)
    {
        _winget = winget;
        _registry = registry;
        _store = store;
        _log = log;
    }

    public async Task<ScanResult> ScanAsync(IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        progress?.Report(new ScanProgress("Looking for winget packages", 0, 4));

        IReadOnlyList<WingetPackage> wingetPackages;
        try
        {
            wingetPackages = await _winget.ListInstalledAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Write(LogLevel.Warning, "winget inventory failed; continuing with registry and Store.", ex);
            warnings.Add("winget inventory could not be read. Matching will be less accurate.");
            wingetPackages = Array.Empty<WingetPackage>();
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new ScanProgress("Reading installed programs", 1, 4));
        IReadOnlyList<ScannedApp> registryApps;
        try
        {
            registryApps = await Task.Run(_registry.Scan, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Write(LogLevel.Error, "Registry scan failed.", ex);
            warnings.Add("The registry could not be read. Try running CTRL+Z as administrator.");
            registryApps = Array.Empty<ScannedApp>();
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new ScanProgress("Reading Microsoft Store apps", 2, 4));
        IReadOnlyList<ScannedApp> storeApps;
        try
        {
            storeApps = await _store.ScanAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Write(LogLevel.Warning, "Store scan failed; continuing.", ex);
            warnings.Add("Microsoft Store apps could not be read. Windows may be restricting Appx queries.");
            storeApps = Array.Empty<ScannedApp>();
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new ScanProgress("Matching apps to winget", 3, 4));

        var registryResults = await Task.Run(
            () => registryApps.Select(app => ApplyMatch(app, wingetPackages)).ToList(),
            cancellationToken).ConfigureAwait(false);

        var merged = new List<ScannedApp>(registryResults);
        var storeResultCount = 0;
        foreach (var storeApp in storeApps)
        {
            var existing = merged.FirstOrDefault(r =>
                r.IsStoreApp == false &&
                WingetMatcher.NormalizeName(r.Name).Equals(WingetMatcher.NormalizeName(storeApp.Name), StringComparison.Ordinal));
            if (existing is not null)
            {
                existing.IsStoreApp = true;
                existing.IconPath ??= storeApp.IconPath;
                existing.RegistryKey ??= storeApp.RegistryKey;
                if (!existing.IsMatched)
                {
                    var match = WingetMatcher.Match(existing, wingetPackages);
                    existing.MatchKind = match.Kind;
                    existing.MatchScore = match.Score;
                    existing.MatchReason = match.Reason;
                    existing.MatchedWingetId = match.PackageId;
                }
                continue;
            }

            storeResultCount++;
            merged.Add(ApplyMatch(storeApp, wingetPackages));
        }

        var wingetOnly = 0;
        var representedIds = merged.Where(a => a.IsMatched)
            .Select(a => a.MatchedWingetId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var representedNames = merged
            .Select(a => a.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var normalizedNames = merged
            .Select(a => WingetMatcher.NormalizeName(a.Name))
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var package in wingetPackages)
        {
            if (representedIds.Contains(package.Id))
            {
                continue;
            }

            if (representedNames.Contains(package.Name) ||
                normalizedNames.Contains(WingetMatcher.NormalizeName(package.Name)))
            {
                continue;
            }

            // Store package ids are product ids; the human name from Get-StartApps is
            // usually better than the winget row name, but both are usable here.
            var isStore = string.Equals(package.Source, "msstore", StringComparison.OrdinalIgnoreCase);
            merged.Add(new ScannedApp
            {
                Name = package.Name,
                Version = package.Version,
                Source = isStore ? AppSource.MicrosoftStore : AppSource.Winget,
                IsStoreApp = isStore,
                Category = AppClassifier.Classify(package.Name),
                PackageId = package.Id,
                MatchedWingetId = package.Id,
                MatchKind = WingetMatchKind.Exact,
                MatchScore = 100,
                MatchReason = "Reported directly by winget."
            });
            representedIds.Add(package.Id);
            representedNames.Add(package.Name);
            wingetOnly++;
        }

        var apps = Deduplicate(merged);
        apps.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));

        var result = new ScanResult
        {
            Apps = apps,
            RegistryCount = registryApps.Count,
            StoreCount = storeResultCount,
            WingetOnlyCount = wingetOnly,
            Warnings = warnings,
            WingetVersion = _winget.Version
        };

        _log.Write(LogLevel.Info,
            $"Scan complete: {result.Apps.Count} apps, {result.MatchedCount} matched, {result.UnmatchedCount} unmatched.");
        progress?.Report(new ScanProgress(
            "Scan complete", 4, 4, $"{result.Apps.Count} apps found", IsComplete: true));
        return result;
    }

    private static ScannedApp ApplyMatch(ScannedApp app, IReadOnlyList<WingetPackage> packages)
    {
        var match = WingetMatcher.Match(app, packages);
        app.MatchKind = match.Kind;
        app.MatchScore = match.Score;
        app.MatchReason = match.Reason;
        app.MatchedWingetId = match.PackageId;
        if (app.MatchKind == WingetMatchKind.Weak && app.Notes is null)
        {
            app.Notes = "Weak winget match; review before installing.";
        }

        return app;
    }

    private static List<ScannedApp> Deduplicate(IEnumerable<ScannedApp> apps)
    {
        var result = new List<ScannedApp>();
        var byKey = new Dictionary<string, ScannedApp>(StringComparer.OrdinalIgnoreCase);

        foreach (var app in apps)
        {
            var key = $"{app.Name.Trim()}|{app.Version?.Trim()}";
            if (byKey.TryGetValue(key, out var existing))
            {
                // Keep the richer record: a matched winget id, a real icon, and Store provenance.
                if (!existing.IsMatched && app.IsMatched)
                {
                    existing.MatchedWingetId = app.MatchedWingetId;
                    existing.MatchKind = app.MatchKind;
                    existing.MatchScore = app.MatchScore;
                    existing.MatchReason = app.MatchReason;
                    existing.PackageId = app.PackageId;
                }

                existing.IsStoreApp |= app.IsStoreApp;
                existing.IconPath ??= app.IconPath;
                existing.InstallLocation ??= app.InstallLocation;
                continue;
            }

            byKey[key] = app;
            result.Add(app);
        }

        return result;
    }
}
