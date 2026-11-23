// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Localization;
using CtrlZ.Core.Models;
using CtrlZ.Core.Services;

namespace CtrlZ.Core.Safety;

/// <summary>
/// Compares a saved profile with what is actually installed.
/// <para>
/// Apps are matched by package id first, then by normalised name and publisher. When two
/// installed apps look equally similar the row is reported as ambiguous rather than guessed,
/// so the user can link it by hand. Nothing here changes the profile; the caller applies the
/// actions the user picks.
/// </para>
/// </summary>
public sealed class DriftAnalyzer
{
    public DriftReport Analyze(DriftRequest request, CancellationToken cancellationToken = default)
    {
        var installed = request.Installed
            .Where(app => app is not null)
            .GroupBy(app => app.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<DriftEntry>();

        foreach (var saved in request.Profile.Apps)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var match = FindMatch(saved, installed, claimed);
            if (match.App is null)
            {
                entries.Add(MissingEntry(saved, match));
                continue;
            }

            claimed.Add(match.App.Id);

            var outdated = BuildOutdated(saved, match.App, request.Winget);
            if (outdated is not null)
            {
                entries.Add(outdated);
            }
        }

        foreach (var app in installed.Where(app => !claimed.Contains(app.Id)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Add(ExtraEntry(app));
        }

        var ignored = new HashSet<string>(request.IgnoredKeys, StringComparer.OrdinalIgnoreCase);
        var ignoredCount = entries.Count(entry => ignored.Contains(entry.Key));

        return new DriftReport
        {
            Entries = entries.Where(entry => !ignored.Contains(entry.Key)).ToList(),
            IgnoredCount = ignoredCount
        };
    }

    /// <summary>Stable identity used for "ignore permanently"; survives profile re-imports.</summary>
    public static string BuildKey(DriftKind kind, string? packageId, string name)
    {
        var identity = string.IsNullOrWhiteSpace(packageId)
            ? WingetMatcher.NormalizeName(name)
            : packageId.Trim().ToLowerInvariant();

        return $"{kind.ToString().ToLowerInvariant()}:{identity}";
    }

    /// <summary>Converts a scanned app into a profile entry, used by "add to profile".</summary>
    public static AppEntry ToProfileEntry(ScannedApp app) => new()
    {
        Name = app.Name?.Trim() ?? string.Empty,
        Source = app.Source,
        Category = app.Category,
        Publisher = app.Publisher,
        Version = app.Version,
        PackageId = string.IsNullOrWhiteSpace(app.PackageId) ? app.MatchedWingetId : app.PackageId,
        InstallLocation = app.InstallLocation,
        IconPath = app.IconPath,
        Notes = CoreText.Get("DriftAnalyzer_AddedFromDriftDetection")
    };

    private static DriftEntry MissingEntry(AppEntry saved, MatchResult match) => new()
    {
        Kind = DriftKind.Missing,
        Key = BuildKey(DriftKind.Missing, saved.PackageId, saved.Name),
        Name = saved.Name,
        Publisher = saved.Publisher,
        PackageId = saved.PackageId,
        ProfileVersion = saved.Version,
        Source = saved.Source,
        Confidence = match.Confidence,
        MatchReason = match.Reason,
        ProfileEntry = saved
    };

    private static DriftEntry ExtraEntry(ScannedApp app)
    {
        var packageId = string.IsNullOrWhiteSpace(app.PackageId) ? app.MatchedWingetId : app.PackageId;
        return new DriftEntry
        {
            Kind = DriftKind.Extra,
            Key = BuildKey(DriftKind.Extra, packageId, app.Name),
            Name = app.Name,
            Publisher = app.Publisher,
            PackageId = packageId,
            InstalledVersion = app.Version,
            Source = app.Source,
            Confidence = DriftConfidence.None,
            MatchReason = CoreText.Get("DriftAnalyzer_InstalledButNotSavedIn"),
            Installed = app
        };
    }

    private static DriftEntry? BuildOutdated(AppEntry saved, ScannedApp installed, IReadOnlyList<WingetPackage> winget)
    {
        var packageId = FirstNonEmpty(installed.PackageId, installed.MatchedWingetId, saved.PackageId);
        if (packageId is null)
        {
            return null;
        }

        var package = winget.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, packageId, StringComparison.OrdinalIgnoreCase));

        var available = package?.AvailableVersion;
        var current = FirstNonEmpty(installed.Version, saved.Version);

        // Unknown or unparsable versions are never reported as outdated: better to stay quiet
        // than to tell someone to upgrade something that is already current.
        if (VersionText.Compare(available, current) != VersionComparison.Newer)
        {
            return null;
        }

        return new DriftEntry
        {
            Kind = DriftKind.Outdated,
            Key = BuildKey(DriftKind.Outdated, packageId, installed.Name),
            Name = installed.Name,
            Publisher = installed.Publisher,
            PackageId = packageId,
            ProfileVersion = saved.Version,
            InstalledVersion = current,
            AvailableVersion = available,
            Source = installed.Source,
            Confidence = DriftConfidence.Exact,
            MatchReason = CoreText.Format("DriftAnalyzer_NewerVersionAvailable", available),
            ProfileEntry = saved,
            Installed = installed
        };
    }

    private static MatchResult FindMatch(AppEntry saved, List<ScannedApp> installed, HashSet<string> claimed)
    {
        if (!string.IsNullOrWhiteSpace(saved.PackageId))
        {
            var byPackageId = installed.FirstOrDefault(app =>
                !claimed.Contains(app.Id) &&
                string.Equals(app.PackageId, saved.PackageId, StringComparison.OrdinalIgnoreCase));
            if (byPackageId is not null)
            {
                return new MatchResult(byPackageId, DriftConfidence.Exact, CoreText.Get("DriftAnalyzer_SamePackageId"));
            }

            var byWingetId = installed.FirstOrDefault(app =>
                !claimed.Contains(app.Id) &&
                string.Equals(app.MatchedWingetId, saved.PackageId, StringComparison.OrdinalIgnoreCase));
            if (byWingetId is not null)
            {
                return new MatchResult(byWingetId, DriftConfidence.Exact, CoreText.Get("DriftAnalyzer_SameWingetId"));
            }
        }

        var targetName = WingetMatcher.NormalizeName(saved.Name);
        if (targetName.Length == 0)
        {
            return new MatchResult(null, DriftConfidence.None, CoreText.Get("DriftAnalyzer_SavedAppHasNoUsable"));
        }

        var targetPublisher = WingetMatcher.NormalizePublisher(saved.Publisher);
        var scored = new List<(ScannedApp App, int Score, string Reason, bool Qualifies)>();

        foreach (var app in installed.Where(candidate => !claimed.Contains(candidate.Id)))
        {
            var name = WingetMatcher.NormalizeName(app.Name);
            if (name.Length == 0)
            {
                continue;
            }

            int score;
            string reason;

            bool qualifies;

            if (string.Equals(name, targetName, StringComparison.Ordinal))
            {
                score = 100;
                reason = CoreText.Get("DriftAnalyzer_SameName");
                qualifies = true;
            }
            else if (targetName.Length >= 4 &&
                     (name.Contains(targetName, StringComparison.Ordinal) ||
                      targetName.Contains(name, StringComparison.Ordinal)))
            {
                score = 60;

                // A single shared word must not be enough to claim an app, or "GitHub Source App"
                // would match "GitHub CLI". Two shared words, or a near-identical full name with
                // the same publisher, is.
                var shared = SharedTokens(targetName, name);
                var ratio = shared / (double)Math.Max(TokenCount(targetName), TokenCount(name));
                var samePublisher = targetPublisher.Length > 0 && string.Equals(
                    WingetMatcher.NormalizePublisher(app.Publisher),
                    targetPublisher,
                    StringComparison.Ordinal);

                qualifies = shared >= 2 || (ratio >= 0.75 && samePublisher);
                reason = qualifies ? CoreText.Get("DriftAnalyzer_SimilarName") : CoreText.Get("DriftAnalyzer_SimilarNameOnlyOneShared");
            }
            else
            {
                continue;
            }

            var publisher = WingetMatcher.NormalizePublisher(app.Publisher);
            if (targetPublisher.Length > 0 && publisher.Length > 0)
            {
                if (string.Equals(publisher, targetPublisher, StringComparison.Ordinal))
                {
                    score += 30;
                    reason += CoreText.Get("DriftAnalyzer_AndPublisher");
                }
                else
                {
                    score -= 25;
                    reason += CoreText.Get("DriftAnalyzer_DifferentPublisher");
                }
            }

            scored.Add((app, score, reason, qualifies));
        }

        if (scored.Count == 0)
        {
            var reason = saved.Source is AppSource.Winget or AppSource.MicrosoftStore
                ? CoreText.Get("DriftAnalyzer_NotInstalled")
                : CoreText.Format("DriftAnalyzer_NotFoundByNameCustom", saved.Source.Display());
            return new MatchResult(null, DriftConfidence.None, reason);
        }

        var ordered = scored.OrderByDescending(candidate => candidate.Score).ToList();
        var best = ordered[0];

        if (ordered.Count > 1 && ordered[1].Score == best.Score)
        {
            var tied = ordered.Count(candidate => candidate.Score == best.Score);
            return new MatchResult(null, DriftConfidence.Weak, CoreText.Format("DriftAnalyzer_AmbiguousInstalledAppsLookSimilar", tied));
        }

        // A candidate that does not qualify is reported as uncertain and named, never claimed as a
        // match: claiming it would hide a genuinely missing app and could invent an "outdated" row.
        if (!best.Qualifies)
        {
            return new MatchResult(
                null,
                DriftConfidence.Weak,
                CoreText.Format("DriftAnalyzer_UncertainClosestIsButThe", best.App.Name));
        }

        var confidence = best.Score >= 120 ? DriftConfidence.Exact
            : best.Score >= 100 ? DriftConfidence.Strong
            : DriftConfidence.Weak;

        return new MatchResult(best.App, confidence, best.Reason);
    }

    /// <summary>Number of distinct words the two normalised names have in common.</summary>
    private static int SharedTokens(string first, string second)
    {
        var left = first.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var right = new HashSet<string>(
            second.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            StringComparer.Ordinal);

        return left.Distinct(StringComparer.Ordinal).Count(right.Contains);
    }

    private static int TokenCount(string value) =>
        value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private sealed record MatchResult(ScannedApp? App, DriftConfidence Confidence, string Reason);
}
