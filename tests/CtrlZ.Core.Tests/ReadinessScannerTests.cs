// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Security.Cryptography;
using CtrlZ.Core.Safety;

namespace CtrlZ.Core.Tests;

public class ReadinessScannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_missing_folder_passes()
    {
        using var home = new TestDirectory();
        var request = Request(home, [new UserFolder("Desktop", home.PathOf("missing"))]);

        var check = Scan(request).Checks.Single(c => c.Id.StartsWith("folder."));

        Assert.Equal(ReadinessStatus.Pass, check.Status);
        Assert.Contains("Not present", check.Summary);
    }

    [Fact]
    public void A_large_folder_with_no_backup_warns()
    {
        using var home = new TestDirectory();
        home.Write("Desktop/big.bin", new string('x', 40));
        var request = Request(home, [new UserFolder("Desktop", home.PathOf("Desktop"))]) with
        {
            LargeFolderBytes = 10
        };

        var check = Scan(request).Checks.Single(c => c.Id == "folder.desktop");

        Assert.Equal(ReadinessStatus.Warning, check.Status);
        Assert.Contains("Large", check.Summary);
        Assert.Contains(check.Details, line => line.Contains("No CTRL+Z backup covers", StringComparison.Ordinal));
    }

    [Fact]
    public void Recently_changed_content_warns_even_when_it_is_small()
    {
        using var home = new TestDirectory();
        var file = home.Write("Documents/recent.txt", "x");
        File.SetLastWriteTimeUtc(file, Now.UtcDateTime.AddDays(-1));
        var request = Request(home, [new UserFolder("Documents", home.PathOf("Documents"))]);

        var check = Scan(request).Checks.Single(c => c.Id == "folder.documents");

        Assert.Equal(ReadinessStatus.Warning, check.Status);
        Assert.Contains("Recently changed", check.Summary);
    }

    [Fact]
    public void Old_and_small_content_passes_with_a_quiet_note()
    {
        using var home = new TestDirectory();
        var file = home.Write("Music/old.mp3", "x");
        File.SetLastWriteTimeUtc(file, Now.UtcDateTime.AddDays(-90));
        var request = Request(home, [new UserFolder("Music", home.PathOf("Music"))]);

        var check = Scan(request).Checks.Single(c => c.Id == "folder.music");

        Assert.Equal(ReadinessStatus.Pass, check.Status);
        Assert.Contains("No backup", check.Summary);
    }

    [Fact]
    public void A_folder_inside_the_backup_folder_is_reported_as_covered()
    {
        using var home = new TestDirectory();
        var backups = home.CreateDirectory("backups");
        home.Write("backups/big.bin", new string('x', 40));
        var request = Request(home, [new UserFolder("Backups", backups)]) with
        {
            BackupFolder = backups,
            LargeFolderBytes = 10
        };

        var check = Scan(request).Checks.Single(c => c.Id == "folder.backups");

        Assert.Equal(ReadinessStatus.Pass, check.Status);
        Assert.Contains("Covered", check.Summary);
    }

    [Fact]
    public void A_detected_browser_without_a_backup_warns()
    {
        using var home = new TestDirectory();
        var request = Request(home, []) with
        {
            Browsers = [new DetectedBrowserSummary("Chrome", 2)]
        };

        var check = Scan(request).Checks.Single(c => c.Id == "browser.chrome");

        Assert.Equal(ReadinessStatus.Warning, check.Status);
        Assert.Contains("No CTRL+Z backup", check.Summary);
    }

    [Fact]
    public void A_fresh_backup_passes_and_a_stale_one_warns()
    {
        using var home = new TestDirectory();
        var freshFolder = home.CreateDirectory("backups-fresh");
        var staleFolder = home.CreateDirectory("backups-stale");

        // Fresh: stamped the same day as "now".
        home.Write("backups-fresh/CTRLZ-Chrome-20260615-1000.zip", "x");
        var fresh = Scan(Request(home, []) with
        {
            BackupFolder = freshFolder,
            Browsers = [new DetectedBrowserSummary("Chrome", 1)]
        }).Checks.Single(c => c.Id == "browser.chrome");
        Assert.Equal(ReadinessStatus.Pass, fresh.Status);

        // Stale: the only archive is 60 days old.
        home.Write("backups-stale/CTRLZ-Chrome-20260416-1000.zip", "x");
        var stale = Scan(Request(home, []) with
        {
            BackupFolder = staleFolder,
            Browsers = [new DetectedBrowserSummary("Chrome", 1)]
        }).Checks.Single(c => c.Id == "browser.chrome");
        Assert.Equal(ReadinessStatus.Warning, stale.Status);
        Assert.Contains("old", stale.Summary);
    }

    [Fact]
    public void No_browsers_detected_yields_a_single_passing_row()
    {
        using var home = new TestDirectory();

        var checks = Scan(Request(home, [])).Checks;

        var browser = Assert.Single(checks, c => c.Id == "browsers");
        Assert.Equal(ReadinessStatus.Pass, browser.Status);
    }

    [Fact]
    public void A_missing_profile_warns()
    {
        using var home = new TestDirectory();

        var check = Scan(Request(home, []) with { ProfileUpdatedUtc = null })
            .Checks.Single(c => c.Id == "profile");

        Assert.Equal(ReadinessStatus.Warning, check.Status);
        Assert.Contains("No saved profile", check.Summary);
    }

    [Fact]
    public void Profile_drift_is_summarised_and_links_to_the_drift_view()
    {
        using var home = new TestDirectory();
        var request = Request(home, []) with
        {
            ProfileUpdatedUtc = Now.AddDays(-3),
            ProfileAppCount = 12,
            Drift = new DriftSummary(Missing: 2, Extra: 3, Outdated: 1, Ignored: 0)
        };

        var check = Scan(request).Checks.Single(c => c.Id == "profile");

        Assert.Equal(ReadinessStatus.Warning, check.Status);
        Assert.True(check.LinksToDrift);
        Assert.Contains("2 saved app", check.Summary);
        Assert.Contains(check.Details, line => line.Contains("missing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Every_reminder_is_emitted_and_needs_the_user()
    {
        using var home = new TestDirectory();

        var reminders = Scan(Request(home, [])).Checks
            .Where(c => c.Id.StartsWith(ReadinessReminders.IdPrefix, StringComparison.Ordinal))
            .ToList();

        Assert.Equal(ReadinessReminders.All.Count, reminders.Count);
        Assert.All(reminders, reminder => Assert.Equal(ReadinessStatus.Warning, reminder.Status));
        Assert.All(reminders, reminder => Assert.False(string.IsNullOrWhiteSpace(reminder.WhatItMeans)));
        Assert.All(reminders, reminder => Assert.False(string.IsNullOrWhiteSpace(reminder.SuggestedAction)));
    }

    [Fact]
    public void Overall_status_reflects_the_worst_check()
    {
        using var home = new TestDirectory();
        home.Write("Desktop/a.txt", "x");

        var report = Scan(Request(home, [new UserFolder("Desktop", home.PathOf("Desktop"))]));

        Assert.Equal(ReadinessStatus.Warning, report.Overall);
        Assert.True(report.WarningCount > 0);
        Assert.Equal(report.Checks.Count, report.PassCount + report.WarningCount + report.FailCount);
    }

    [Fact]
    public void A_full_scan_never_modifies_anything()
    {
        using var home = new TestDirectory();
        home.Write("Desktop/keep.txt", "alpha");
        home.Write("Documents/sub/keep2.txt", "bravo");
        home.Write("backups/CTRLZ-Chrome-20260615-1000.zip", "archive");
        var before = Snapshot(home.Root);

        var request = Request(home,
        [
            new UserFolder("Desktop", home.PathOf("Desktop")),
            new UserFolder("Documents", home.PathOf("Documents")),
            new UserFolder("Downloads", home.PathOf("Downloads"))
        ]) with
        {
            BackupFolder = home.PathOf("backups"),
            Browsers = [new DetectedBrowserSummary("Chrome", 1)]
        };

        _ = Scan(request);

        Assert.Equal(before, Snapshot(home.Root));
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        using var home = new TestDirectory();
        home.Write("Desktop/a.txt", "x");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new ReadinessScanner().ScanAsync(
                Request(home, [new UserFolder("Desktop", home.PathOf("Desktop"))]),
                null,
                cts.Token));
    }

    // ---- helpers ------------------------------------------------------------------

    private static ReadinessReport Scan(ReadinessRequest request) => new ReadinessScanner().Scan(request);

    private static ReadinessRequest Request(TestDirectory home, IReadOnlyList<UserFolder> folders) => new()
    {
        Environment = new SafetyEnvironment
        {
            HomeDirectory = home.PathOf("home"),
            AppData = home.PathOf("home"),
            LocalAppData = home.PathOf("home"),
            ProgramData = home.PathOf("home"),
            ProgramFiles = home.PathOf("home"),
            ProgramFilesX86 = home.PathOf("home"),
            Documents = home.PathOf("Documents"),
            SavedGames = home.PathOf("SavedGames"),
            FixedDrives = []
        },
        UserFolders = folders,
        ProfileUpdatedUtc = Now.AddDays(-1),
        ProfileAppCount = 5,
        Now = Now,
        LargeFolderBytes = 1024L * 1024 * 1024,
        RecentDays = 7,
        FolderOptions = new FolderUsageOptions { MaxDepth = 8, MaxFiles = 10_000, MaxDuration = TimeSpan.Zero }
    };

    private static List<string> Snapshot(string root)
    {
        var entries = new List<string>();
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).OrderBy(p => p))
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                entries.Add($"dir|{path}|{Directory.GetLastWriteTimeUtc(path).Ticks}");
                continue;
            }

            using var stream = File.OpenRead(path);
            entries.Add($"file|{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{Convert.ToHexString(SHA256.HashData(stream))}");
        }

        return entries;
    }
}
