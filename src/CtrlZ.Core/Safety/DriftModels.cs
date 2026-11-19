// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Models;
using CtrlZ.Core.Services;

namespace CtrlZ.Core.Safety;

/// <summary>Which drift group an entry belongs to.</summary>
public enum DriftKind
{
    Missing,
    Extra,
    Outdated
}

/// <summary>How sure the matcher is that a saved app and an installed app are the same thing.</summary>
public enum DriftConfidence
{
    /// <summary>Same package id, or same normalised name and publisher.</summary>
    Exact,

    /// <summary>Same normalised name; publisher missing or different.</summary>
    Strong,

    /// <summary>Only a name overlap, or several candidates looked equally similar.</summary>
    Weak,

    /// <summary>No match at all.</summary>
    None
}

/// <summary>Outcome of comparing an available version with an installed one.</summary>
public enum VersionComparison
{
    Newer,
    Same,
    Older,
    Unknown
}

/// <summary>One row of the drift report.</summary>
public sealed record DriftEntry
{
    public required DriftKind Kind { get; init; }
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string? Publisher { get; init; }
    public string? PackageId { get; init; }
    public string? ProfileVersion { get; init; }
    public string? InstalledVersion { get; init; }
    public string? AvailableVersion { get; init; }
    public AppSource Source { get; init; } = AppSource.Winget;
    public DriftConfidence Confidence { get; init; } = DriftConfidence.None;
    public string MatchReason { get; init; } = string.Empty;

    /// <summary>The saved profile entry, for Missing and Outdated rows.</summary>
    public AppEntry? ProfileEntry { get; init; }

    /// <summary>The installed app, for Extra and Outdated rows.</summary>
    public ScannedApp? Installed { get; init; }
}

/// <summary>Drift between the saved profile and this PC.</summary>
public sealed record DriftReport
{
    public IReadOnlyList<DriftEntry> Entries { get; init; } = [];

    /// <summary>Entries the user asked never to see again; counted but not returned.</summary>
    public int IgnoredCount { get; init; }

    public IReadOnlyList<DriftEntry> Missing => Entries.Where(entry => entry.Kind == DriftKind.Missing).ToList();

    public IReadOnlyList<DriftEntry> Extra => Entries.Where(entry => entry.Kind == DriftKind.Extra).ToList();

    public IReadOnlyList<DriftEntry> Outdated => Entries.Where(entry => entry.Kind == DriftKind.Outdated).ToList();

    public bool IsEmpty => Entries.Count == 0;

    public DriftSummary ToSummary() =>
        new(Missing.Count, Extra.Count, Outdated.Count, IgnoredCount);
}

/// <summary>Inputs for one drift comparison.</summary>
public sealed record DriftRequest
{
    public required AppProfile Profile { get; init; }

    /// <summary>Apps installed on this PC, as produced by the scanner.</summary>
    public required IReadOnlyList<ScannedApp> Installed { get; init; }

    /// <summary>winget's own list, used only for the available-version column.</summary>
    public IReadOnlyList<WingetPackage> Winget { get; init; } = [];

    public IReadOnlyList<string> IgnoredKeys { get; init; } = [];
}

/// <summary>
/// Lenient version comparison. Windows apps use anything from four-part versions to dates,
/// so this compares leading numeric segments and says "unknown" rather than guessing.
/// </summary>
public static class VersionText
{
    public static VersionComparison Compare(string? available, string? installed)
    {
        if (string.IsNullOrWhiteSpace(available) || string.IsNullOrWhiteSpace(installed))
        {
            return VersionComparison.Unknown;
        }

        var left = available.Trim();
        var right = installed.Trim();

        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            return VersionComparison.Same;
        }

        if (!TryParseLeadingNumbers(left, out var a) || !TryParseLeadingNumbers(right, out var b))
        {
            return VersionComparison.Unknown;
        }

        var length = Math.Max(a.Length, b.Length);
        for (var i = 0; i < length; i++)
        {
            var av = i < a.Length ? a[i] : 0;
            var bv = i < b.Length ? b[i] : 0;
            if (av != bv)
            {
                return av > bv ? VersionComparison.Newer : VersionComparison.Older;
            }
        }

        // Numerically equal but textually different (for example "1.2" versus "1.2.0").
        return VersionComparison.Same;
    }

    /// <summary>Reads the leading dotted numbers, stopping at the first non-numeric segment.</summary>
    public static bool TryParseLeadingNumbers(string value, out int[] parts)
    {
        parts = [];
        var numbers = new List<int>();

        foreach (var segment in value.Split('.', '-', '+', ' '))
        {
            if (segment.Length == 0)
            {
                break;
            }

            if (!int.TryParse(segment, out var number))
            {
                break;
            }

            numbers.Add(number);
        }

        if (numbers.Count == 0)
        {
            return false;
        }

        parts = numbers.ToArray();
        return true;
    }
}
