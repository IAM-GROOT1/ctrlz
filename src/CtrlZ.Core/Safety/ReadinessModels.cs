// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

namespace CtrlZ.Core.Safety;

/// <summary>Outcome of a single readiness check.</summary>
public enum ReadinessStatus
{
    Pass,
    Warning,
    Fail
}

/// <summary>
/// One row of the "Ready to wipe?" report. The scanner produces these; handled/ignored
/// flags are applied by the caller from <see cref="SafetyState"/> so the scan stays pure.
/// </summary>
public sealed record ReadinessCheck
{
    public required string Id { get; init; }

    /// <summary>Short heading, for example "Games and launchers".</summary>
    public required string Title { get; init; }

    public required ReadinessStatus Status { get; init; }

    /// <summary>One-line result, for example "Steam detected with 143 games".</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>Plain-language explanation of what this means for the user.</summary>
    public string WhatItMeans { get; init; } = string.Empty;

    /// <summary>What the user should do about it.</summary>
    public string SuggestedAction { get; init; } = string.Empty;

    /// <summary>Supporting lines: counts, sizes and folder roles only, never personal file names.</summary>
    public IReadOnlyList<string> Details { get; init; } = [];

    /// <summary>True when this check can hand off to the profile drift view.</summary>
    public bool LinksToDrift { get; init; }
}

/// <summary>Whole checklist result for one scan.</summary>
public sealed record ReadinessReport
{
    public DateTimeOffset GeneratedUtc { get; init; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<ReadinessCheck> Checks { get; init; } = [];

    public int PassCount => Checks.Count(c => c.Status == ReadinessStatus.Pass);

    public int WarningCount => Checks.Count(c => c.Status == ReadinessStatus.Warning);

    public int FailCount => Checks.Count(c => c.Status == ReadinessStatus.Fail);

    /// <summary>The worst status in the report; a clean report is <see cref="ReadinessStatus.Pass"/>.</summary>
    public ReadinessStatus Overall =>
        Checks.Any(c => c.Status == ReadinessStatus.Fail) ? ReadinessStatus.Fail
        : Checks.Any(c => c.Status == ReadinessStatus.Warning) ? ReadinessStatus.Warning
        : ReadinessStatus.Pass;
}

/// <summary>Result of walking one folder. Counts and sizes only; no paths of individual files.</summary>
public sealed record FolderUsage
{
    public required string Path { get; init; }
    public bool Exists { get; init; }
    public long TotalBytes { get; init; }
    public int FileCount { get; init; }

    /// <summary>Newest write time seen while walking, which is what "recently changed" means here.</summary>
    public DateTimeOffset? LastWriteUtc { get; init; }

    /// <summary>True when at least one folder could not be read (permissions or IO error).</summary>
    public bool AccessDenied { get; init; }

    /// <summary>
    /// True when the walk stopped early: depth cap, file cap or time budget. The counts are
    /// then a lower bound, and the UI says so rather than pretending the folder is empty.
    /// </summary>
    public bool Truncated { get; init; }
}

/// <summary>Safety limits for <see cref="FolderUsageScanner"/>. Every limit is optional.</summary>
public sealed record FolderUsageOptions
{
    /// <summary>How many levels below the root to descend. Level 1 is the root's own children.</summary>
    public int MaxDepth { get; init; } = 6;

    /// <summary>Stop after this many files; the result is then marked truncated.</summary>
    public int MaxFiles { get; init; } = 50_000;

    /// <summary>Wall-clock budget for one folder; zero or negative disables the budget.</summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Junctions and symlinks are skipped by default so loops cannot hang the scan.</summary>
    public bool FollowReparsePoints { get; init; }

    public static FolderUsageOptions Default { get; } = new();
}
