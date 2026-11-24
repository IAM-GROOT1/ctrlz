// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Text.RegularExpressions;
using CtrlZ.Core.Localization;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Services;

/// <summary>
/// Conservative fuzzy matching between registry display names and winget package ids.
/// False positives are worse than an entry in the Unmatched group, so scores below
/// 75 are rejected. Matching is deterministic and unit tested.
/// </summary>
public static partial class WingetMatcher
{
    [GeneratedRegex(@"\b(v|version)?\d+(\.\d+){1,4}([a-z]\d+)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex VersionTokenRegex();

    [GeneratedRegex(@"\b(x64|x86|arm64|32[- ]?bit|64[- ]?bit|amd64)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ArchitectureRegex();

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "for", "and", "with", "app", "application", "desktop", "client", "free",
        "edition", "installer", "setup", "official", "latest", "windows", "win"
    };

    public static WingetMatch Match(ScannedApp app, IReadOnlyList<WingetPackage> packages)
    {
        return Match(app.Name, app.Publisher, app.Version, packages);
    }

    public static WingetMatch Match(string name, string? publisher, string? version, IReadOnlyList<WingetPackage> packages)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new WingetMatch(null, WingetMatchKind.None, 0, CoreText.Get("WingetMatcher_NoName"));
        }

        if (packages.Count == 0)
        {
            return new WingetMatch(null, WingetMatchKind.None, 0, CoreText.Get("WingetMatcher_NoPackages"));
        }

        var normalizedName = NormalizeName(name);
        var normalizedPublisher = NormalizePublisher(publisher);
        WingetPackage? bestPackage = null;
        var bestScore = 0d;
        var bestReason = string.Empty;

        foreach (var package in packages)
        {
            var packageName = NormalizeName(package.Name);
            var packageIdName = NormalizeName(LastIdSegment(package.Id));
            var nameScore = Math.Max(Similarity(normalizedName, packageName), Similarity(normalizedName, packageIdName));
            var score = nameScore * 78;

            if (normalizedName.Equals(packageName, StringComparison.Ordinal))
            {
                score += 14;
            }
            else if (normalizedName.Equals(packageIdName, StringComparison.Ordinal))
            {
                score += 10;
            }

            if (normalizedPublisher.Length > 0)
            {
                var idOrName = package.Id + " " + package.Name;
                if (idOrName.Contains(normalizedPublisher, StringComparison.OrdinalIgnoreCase))
                {
                    score += 7;
                }
            }

            // A matching installed version is weak evidence but useful for similarly-named apps.
            if (!string.IsNullOrWhiteSpace(version) && !string.IsNullOrWhiteSpace(package.Version) &&
                version.Equals(package.Version, StringComparison.OrdinalIgnoreCase))
            {
                score += 2;
            }

            // Prefer first-party winget manifests when a Store and winget row describe the same app.
            if (string.Equals(package.Source, "msstore", StringComparison.OrdinalIgnoreCase))
            {
                score -= 1;
            }

            score = Math.Clamp(score, 0, 100);
            if (score > bestScore)
            {
                bestScore = score;
                bestPackage = package;
                bestReason = BuildReason(normalizedName, packageName, package);
            }
        }

        bestScore = Math.Round(bestScore, 1);
        if (bestPackage is null || bestScore < 75)
        {
            return new WingetMatch(null, WingetMatchKind.None, bestScore,
                bestPackage is null
                    ? CoreText.Get("WingetMatcher_NoCandidate")
                    : CoreText.Format("WingetMatcher_BestCandidate", bestPackage.Id, bestScore));
        }

        var kind = bestScore >= 96 ? WingetMatchKind.Exact
            : bestScore >= 86 ? WingetMatchKind.Strong
            : WingetMatchKind.Weak;
        return new WingetMatch(bestPackage.Id, kind, bestScore, bestReason);
    }

    public static string NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = value.ToLowerInvariant()
            .Replace("&", " and ", StringComparison.Ordinal)
            .Replace("+", " plus ", StringComparison.Ordinal)
            .Replace("’", "'", StringComparison.Ordinal);

        // Parenthesised architecture/edition text is usually noise for name matching.
        text = Regex.Replace(text, @"\([^)]*\)", " ");
        text = VersionTokenRegex().Replace(text, " ");
        text = ArchitectureRegex().Replace(text, " ");
        text = Regex.Replace(text, @"\b(microsoft|corp|corporation|inc|ltd|llc|gmbh|technologies|software)\b", " ");

        var tokens = Regex.Split(text, @"[^a-z0-9]+")
            .Where(t => t.Length > 0 && !StopWords.Contains(t))
            .Where(t => !(t.Length == 1 && !char.IsDigit(t[0])))
            .Select(CanonicalToken)
            .ToArray();

        return string.Join(' ', tokens);
    }

    public static string NormalizePublisher(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"\b(microsoft|corp|corporation|inc|ltd|llc|gmbh|technologies|software)\b", " ");
        return string.Join(' ', Regex.Split(normalized, @"[^a-z0-9]+").Where(t => t.Length > 1));
    }

    private static double Similarity(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0)
        {
            return 0;
        }

        if (left.Equals(right, StringComparison.Ordinal))
        {
            return 1;
        }

        var shorter = left.Length <= right.Length ? left : right;
        var longer = left.Length <= right.Length ? right : left;
        var lengthRatio = (double)shorter.Length / longer.Length;
        if (lengthRatio < 0.45)
        {
            return 0;
        }

        if (longer.Contains(shorter, StringComparison.Ordinal))
        {
            return 0.9 * lengthRatio + 0.1;
        }

        var leftTokens = left.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var rightTokens = right.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if (leftTokens.Count == 0 || rightTokens.Count == 0)
        {
            return 0;
        }

        var intersection = leftTokens.Intersect(rightTokens, StringComparer.Ordinal).Count();
        var union = leftTokens.Union(rightTokens, StringComparer.Ordinal).Count();
        var jaccard = (double)intersection / union;

        // Reward shared leading token, which is usually the product family name.
        var leading = leftTokens.First().Equals(rightTokens.First(), StringComparison.Ordinal) ? 0.08 : 0;
        return Math.Min(1, jaccard + leading);
    }

    private static string BuildReason(string normalizedName, string packageName, WingetPackage package)
    {
        if (normalizedName.Equals(packageName, StringComparison.Ordinal))
        {
            return CoreText.Get("WingetMatcher_ExactName");
        }

        return CoreText.Format("WingetMatcher_NameSimilarity", package.Name);
    }

    private static string LastIdSegment(string id)
    {
        var slash = id.LastIndexOf('/');
        var dot = id.LastIndexOf('.');
        var index = Math.Max(slash, dot);
        return index >= 0 && index < id.Length - 1 ? id[(index + 1)..] : id;
    }

    private static string CanonicalToken(string token) => token switch
    {
        "vscode" => "visual studio code",
        "vs" => "visual studio",
        "ms" => "microsoft",
        "onedrive" => "onedrive",
        "7zip" => "7 zip",
        _ => token
    };
}
