// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Models;
using CtrlZ.Core.Safety;
using CtrlZ.Core.Services;

namespace CtrlZ.Core.Tests;

public class DriftAnalyzerTests
{
    [Fact]
    public void Matching_by_package_id_is_exact()
    {
        var report = Analyze(
            profile: [Saved("Notepad++", packageId: "Notepad++.Notepad++")],
            installed: [Installed("Notepad++", packageId: "Notepad++.Notepad++", version: "8.6")]);

        Assert.Empty(report.Missing);
        Assert.Empty(report.Extra);
        Assert.Empty(report.Outdated);
    }

    [Fact]
    public void Matching_by_normalised_name_and_publisher_is_exact()
    {
        var report = Analyze(
            profile: [Saved("Google Chrome", publisher: "Google LLC")],
            installed: [Installed("Google  Chrome", publisher: "Google LLC")]);

        Assert.Empty(report.Missing);
        Assert.Empty(report.Extra);
    }

    [Fact]
    public void A_similar_name_is_matched_with_low_confidence()
    {
        var report = Analyze(
            profile: [Saved("Visual Studio Code")],
            installed: [Installed("Microsoft Visual Studio Code Insiders")]);

        Assert.Empty(report.Missing);
        Assert.Empty(report.Extra);
    }

    [Fact]
    public void Two_equally_similar_apps_are_ambiguous_not_guessed()
    {
        var report = Analyze(
            profile: [Saved("Steam", publisher: "Valve")],
            installed:
            [
                Installed("Steam", publisher: "Valve", version: "1.0"),
                Installed("Steam", publisher: "Valve", version: "2.0")
            ]);

        var missing = Assert.Single(report.Missing);
        Assert.Contains("Ambiguous", missing.MatchReason);
        Assert.Equal(DriftConfidence.Weak, missing.Confidence);

        // Neither candidate is silently claimed: both stay visible as extra.
        Assert.Equal(2, report.Extra.Count);
    }

    [Fact]
    public void A_saved_app_that_is_not_installed_is_missing()
    {
        var report = Analyze(
            profile: [Saved("7-Zip", packageId: "7zip.7zip")],
            installed: []);

        var missing = Assert.Single(report.Missing);
        Assert.Equal("7-Zip", missing.Name);
        Assert.Equal("7zip.7zip", missing.PackageId);
        Assert.Equal("Not installed", missing.MatchReason);
        Assert.NotNull(missing.ProfileEntry);
    }

    [Fact]
    public void An_installed_app_not_in_the_profile_is_extra()
    {
        var report = Analyze(
            profile: [],
            installed: [Installed("Slack", packageId: "SlackTechnologies.Slack", version: "4.40")]);

        var extra = Assert.Single(report.Extra);
        Assert.Equal("Slack", extra.Name);
        Assert.Equal("SlackTechnologies.Slack", extra.PackageId);
        Assert.NotNull(extra.Installed);
    }

    [Fact]
    public void A_newer_available_version_marks_the_app_outdated()
    {
        var report = Analyze(
            profile: [Saved("Notepad++", packageId: "Notepad++.Notepad++", version: "8.0")],
            installed: [Installed("Notepad++", packageId: "Notepad++.Notepad++", version: "8.0")],
            winget: [new WingetPackage("Notepad++", "Notepad++.Notepad++", "8.0", "8.6")]);

        var outdated = Assert.Single(report.Outdated);
        Assert.Equal("8.0", outdated.InstalledVersion);
        Assert.Equal("8.6", outdated.AvailableVersion);
        Assert.Empty(report.Missing);
        Assert.Empty(report.Extra);
    }

    [Fact]
    public void An_equal_or_older_available_version_is_not_outdated()
    {
        var report = Analyze(
            profile: [Saved("App", packageId: "Vendor.App", version: "2.0")],
            installed: [Installed("App", packageId: "Vendor.App", version: "2.0")],
            winget: [new WingetPackage("App", "Vendor.App", "2.0", "1.9")]);

        Assert.Empty(report.Outdated);
    }

    [Fact]
    public void Unknown_versions_never_produce_an_outdated_row()
    {
        var report = Analyze(
            profile: [Saved("App", packageId: "Vendor.App", version: "beta")],
            installed: [Installed("App", packageId: "Vendor.App", version: "beta")],
            winget: [new WingetPackage("App", "Vendor.App", "beta", "beta2")]);

        Assert.Empty(report.Outdated);
        Assert.Empty(report.Missing);
    }

    [Fact]
    public void A_missing_app_with_no_version_does_not_throw()
    {
        var report = Analyze(
            profile: [Saved("App", packageId: "Vendor.App", version: null)],
            installed: []);

        Assert.Single(report.Missing);
    }

    [Fact]
    public void A_custom_source_that_is_not_found_explains_why()
    {
        var report = Analyze(
            profile: [Saved("My Tool", source: AppSource.LocalFile)],
            installed: []);

        var missing = Assert.Single(report.Missing);
        Assert.Contains("custom", missing.MatchReason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(AppSource.GitHub)]
    [InlineData(AppSource.DirectUrl)]
    [InlineData(AppSource.LocalFile)]
    public void Custom_sources_still_match_installed_apps_by_name(AppSource source)
    {
        var report = Analyze(
            profile: [Saved("My Tool", source: source)],
            installed: [Installed("My Tool")]);

        Assert.Empty(report.Missing);
    }

    [Fact]
    public void Ignored_keys_are_hidden_but_counted()
    {
        var saved = Saved("7-Zip", packageId: "7zip.7zip");
        var key = DriftAnalyzer.BuildKey(DriftKind.Missing, saved.PackageId, saved.Name);

        var report = Analyze(
            profile: [saved],
            installed: [],
            ignoredKeys: [key]);

        Assert.Empty(report.Entries);
        Assert.Equal(1, report.IgnoredCount);
        Assert.True(report.IsEmpty);
    }

    [Fact]
    public void Keys_are_stable_for_package_ids_and_fall_back_to_the_name()
    {
        Assert.Equal("missing:7zip.7zip", DriftAnalyzer.BuildKey(DriftKind.Missing, "7ZIP.7zip", "Anything"));
        Assert.Equal(
            DriftAnalyzer.BuildKey(DriftKind.Extra, null, "My  Tool!"),
            DriftAnalyzer.BuildKey(DriftKind.Extra, null, "my tool"));
    }

    [Fact]
    public void Scanned_apps_convert_into_profile_entries()
    {
        var scanned = new ScannedApp
        {
            Name = "Slack",
            Publisher = "Slack Technologies",
            Version = "4.40",
            Source = AppSource.Winget,
            MatchedWingetId = "SlackTechnologies.Slack",
            InstallLocation = @"C:\Program Files\Slack"
        };

        var entry = DriftAnalyzer.ToProfileEntry(scanned);

        Assert.Equal("Slack", entry.Name);
        Assert.Equal("SlackTechnologies.Slack", entry.PackageId);
        Assert.Equal("4.40", entry.Version);
        Assert.Equal(AppSource.Winget, entry.Source);
    }

    [Fact]
    public void Summary_counts_mirror_the_groups()
    {
        var report = Analyze(
            profile: [Saved("Gone", packageId: "Vendor.Gone")],
            installed:
            [
                Installed("Extra One", packageId: "Vendor.Extra1"),
                Installed("Extra Two", packageId: "Vendor.Extra2")
            ]);

        var summary = report.ToSummary();
        Assert.Equal(1, summary.Missing);
        Assert.Equal(2, summary.Extra);
        Assert.Equal(0, summary.Outdated);
    }

    // ---- version comparison -------------------------------------------------------

    [Theory]
    [InlineData("8.6", "8.0", VersionComparison.Newer)]
    [InlineData("8.0", "8.6", VersionComparison.Older)]
    [InlineData("8.0", "8.0", VersionComparison.Same)]
    [InlineData("1.2", "1.2.0", VersionComparison.Same)]
    [InlineData("1.10", "1.9", VersionComparison.Newer)]
    [InlineData("4.40.1", "4.40", VersionComparison.Newer)]
    [InlineData("2026.05", "2026.04", VersionComparison.Newer)]
    [InlineData("beta", "1.0", VersionComparison.Unknown)]
    [InlineData(null, "1.0", VersionComparison.Unknown)]
    [InlineData("1.0", null, VersionComparison.Unknown)]
    public void Version_comparison_handles_realistic_version_shapes(
        string? available,
        string? installed,
        VersionComparison expected)
    {
        Assert.Equal(expected, VersionText.Compare(available, installed));
    }

    [Fact]
    public void A_short_version_is_padded_before_comparing()
    {
        Assert.Equal(VersionComparison.Same, VersionText.Compare("2", "2.0.0"));
        Assert.Equal(VersionComparison.Newer, VersionText.Compare("2.0.1", "2"));
    }

    // ---- helpers ------------------------------------------------------------------

    private static DriftReport Analyze(
        IReadOnlyList<AppEntry> profile,
        IReadOnlyList<ScannedApp> installed,
        IReadOnlyList<WingetPackage>? winget = null,
        IReadOnlyList<string>? ignoredKeys = null) =>
        new DriftAnalyzer().Analyze(new DriftRequest
        {
            Profile = new AppProfile { Apps = profile.ToList() },
            Installed = installed,
            Winget = winget ?? [],
            IgnoredKeys = ignoredKeys ?? []
        });

    private static AppEntry Saved(
        string name,
        string? publisher = null,
        string? packageId = null,
        string? version = null,
        AppSource source = AppSource.Winget) => new()
    {
        Name = name,
        Publisher = publisher,
        PackageId = packageId,
        Version = version,
        Source = source
    };

    private static ScannedApp Installed(
        string name,
        string? publisher = null,
        string? packageId = null,
        string? version = null,
        AppSource source = AppSource.Winget) => new()
    {
        Name = name,
        Publisher = publisher,
        PackageId = packageId,
        Version = version,
        Source = source
    };
}
