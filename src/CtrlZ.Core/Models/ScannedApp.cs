// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Localization;

namespace CtrlZ.Core.Models;

/// <summary>A detected application before the user turns it into a profile entry.</summary>
public sealed class ScannedApp
{
    public string Name { get; set; } = string.Empty;
    public string? Publisher { get; set; }
    public string? Version { get; set; }
    public AppSource Source { get; set; } = AppSource.Winget;
    public AppCategory Category { get; set; } = AppCategory.Other;
    public string? PackageId { get; set; }
    public string? InstallLocation { get; set; }
    public string? UninstallString { get; set; }
    public string? IconPath { get; set; }
    public string? RegistryKey { get; set; }
    public bool IsStoreApp { get; set; }

    public WingetMatchKind MatchKind { get; set; } = WingetMatchKind.None;
    public string? MatchReason { get; set; }
    public double MatchScore { get; set; }
    public string? MatchedWingetId { get; set; }
    public string? Notes { get; set; }

    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    public bool IsMatched => !string.IsNullOrWhiteSpace(MatchedWingetId);

    public string SourceLabel => IsStoreApp ? CoreText.Get("Source_Store") : Source.Display();

    public AppEntry ToProfileEntry() => new()
    {
        Name = Name,
        Publisher = Publisher,
        Version = Version,
        Source = IsStoreApp ? AppSource.MicrosoftStore : Source,
        Category = Category,
        PackageId = MatchedWingetId ?? PackageId,
        InstallLocation = InstallLocation,
        IconPath = IconPath,
        Notes = Notes ?? (MatchKind == WingetMatchKind.Weak ? "winget match needs review" : null)
    };
}

public sealed record WingetPackage(string Name, string Id, string? Version, string? AvailableVersion, string? Source = "winget");

public sealed record WingetMatch(string? PackageId, WingetMatchKind Kind, double Score, string Reason);
