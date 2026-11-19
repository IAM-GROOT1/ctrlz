// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Text;
using CtrlZ.Core.Infrastructure;

namespace CtrlZ.Core.Safety;

/// <summary>
/// Builds the "Ready to wipe?" report. Every check is read-only: it sizes folders, counts
/// entries and compares dates, and nothing here writes, moves or deletes a user file. The
/// scan runs on a worker thread with progress and cancellation so the UI stays responsive.
/// </summary>
public sealed class ReadinessScanner
{
    private readonly FolderUsageScanner _folders;
    private readonly DeveloperItemsProbe _developer;
    private readonly GameLibraryProbe _games;
    private readonly BrowserBackupInventory _backups;

    public ReadinessScanner(
        FolderUsageScanner? folders = null,
        DeveloperItemsProbe? developer = null,
        GameLibraryProbe? games = null,
        BrowserBackupInventory? backups = null)
    {
        _folders = folders ?? new FolderUsageScanner();
        _developer = developer ?? new DeveloperItemsProbe();
        _games = games ?? new GameLibraryProbe();
        _backups = backups ?? new BrowserBackupInventory();
    }

    public Task<ReadinessReport> ScanAsync(
        ReadinessRequest request,
        IProgress<ReadinessProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(request, progress, cancellationToken), cancellationToken);

    public ReadinessReport Scan(
        ReadinessRequest request,
        IProgress<ReadinessProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var now = request.Now ?? DateTimeOffset.UtcNow;
        var checks = new List<ReadinessCheck>();

        var total = request.UserFolders.Count
                    + 2                                   // games, developer items
                    + Math.Max(1, request.Browsers.Count) // browser rows (or the "none" row)
                    + 1                                   // profile
                    + ReadinessReminders.All.Count;
        var step = 0;
        void Report(string name) => progress?.Report(new ReadinessProgress(name, ++step, total));

        foreach (var folder in request.UserFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(folder.Label);

            var usage = _folders.Scan(folder.Path, folder.Options ?? request.FolderOptions, cancellationToken);
            checks.Add(BuildFolderCheck(folder, usage, request, now));
        }

        Report("Games and launchers");
        checks.Add(BuildGamesCheck(request, cancellationToken));

        Report("Developer items");
        checks.Add(BuildDeveloperCheck(request, cancellationToken));

        Report("Browsers");
        checks.AddRange(BuildBrowserChecks(request, cancellationToken, now));

        Report("Profile");
        checks.Add(BuildProfileCheck(request, now));

        foreach (var reminder in ReadinessReminders.All)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(reminder.Label);
            checks.Add(new ReadinessCheck
            {
                Id = ReadinessReminders.IdPrefix + reminder.Id,
                Title = reminder.Label,
                Status = ReadinessStatus.Warning,
                Summary = "Confirm this yourself — CTRL+Z cannot check it",
                WhatItMeans = reminder.WhatItMeans,
                SuggestedAction = reminder.SuggestedAction
            });
        }

        return new ReadinessReport { GeneratedUtc = now, Checks = checks };
    }

    private static ReadinessCheck BuildFolderCheck(
        UserFolder folder,
        FolderUsage usage,
        ReadinessRequest request,
        DateTimeOffset now)
    {
        var id = "folder." + Slug(folder.Label);
        const string meaning =
            "These files exist only on this PC. A clean install removes them unless a copy exists somewhere else.";

        if (!usage.Exists)
        {
            return new ReadinessCheck
            {
                Id = id,
                Title = folder.Label,
                Status = ReadinessStatus.Pass,
                Summary = "Not present on this PC",
                WhatItMeans = meaning
            };
        }

        var size = FileUtil.FormatBytes(usage.TotalBytes);
        var covered = IsInsideAnotherFolder(usage.Path, request.BackupFolder);
        var details = new List<string> { $"{usage.FileCount:N0} files · {size}" };

        if (usage.LastWriteUtc is { } last)
        {
            details.Add($"Last change {DescribeAge(now - last)}");
        }

        if (usage.Truncated)
        {
            details.Add("Scan stopped at a limit — counts are a lower bound.");
        }

        if (usage.AccessDenied)
        {
            details.Add("Some folders could not be read, so the size is a lower bound.");
        }

        details.Add(covered
            ? "Inside your CTRL+Z backup folder."
            : "No CTRL+Z backup covers this folder.");

        var large = request.LargeFolderBytes > 0 && usage.TotalBytes >= request.LargeFolderBytes;
        var recent = request.RecentDays > 0 &&
                     usage.LastWriteUtc is { } written &&
                     (now - written).TotalDays <= request.RecentDays;

        var flagged = !covered && (large || recent);

        return new ReadinessCheck
        {
            Id = id,
            Title = folder.Label,
            Status = flagged ? ReadinessStatus.Warning : ReadinessStatus.Pass,
            Summary = covered
                ? "Covered by your backup folder"
                : flagged
                    ? (large && recent ? "Large and recently changed — no backup"
                        : large ? "Large — no backup"
                        : "Recently changed — no backup")
                    : $"No backup, but quiet ({size})",
            WhatItMeans = meaning,
            SuggestedAction = flagged
                ? "Copy this folder to external media, OneDrive or another PC before wiping."
                : string.Empty,
            Details = details
        };
    }

    private ReadinessCheck BuildGamesCheck(ReadinessRequest request, CancellationToken cancellationToken)
    {
        var detected = _games.Detect(request.Environment, cancellationToken)
            .Where(library => library.Detected)
            .ToList();

        return new ReadinessCheck
        {
            Id = "games",
            Title = "Games and launchers",
            Status = detected.Count > 0 ? ReadinessStatus.Warning : ReadinessStatus.Pass,
            Summary = detected.Count > 0
                ? string.Join(", ", detected.Select(library => library.Name)) + " detected"
                : "No game launchers detected",
            WhatItMeans =
                "Launcher libraries and game saves are not backed up by CTRL+Z. Saves that sync to the cloud " +
                "are only safe once the launcher has actually uploaded them.",
            SuggestedAction = detected.Count > 0
                ? "Check each launcher reports its saves as synced, and copy any local saves separately."
                : string.Empty,
            Details = detected
                .Select(library =>
                {
                    var saves = library.SavePaths.Count == 0
                        ? "no save folders found"
                        : $"{library.SavePaths.Count} common save folder{(library.SavePaths.Count == 1 ? string.Empty : "s")} present";
                    return $"{library.Name}: {library.InstallPath} · {saves}";
                })
                .ToList()
        };
    }

    private ReadinessCheck BuildDeveloperCheck(ReadinessRequest request, CancellationToken cancellationToken)
    {
        var present = _developer.Probe(request.Environment, cancellationToken)
            .Where(item => item.Present)
            .ToList();

        return new ReadinessCheck
        {
            Id = "developer",
            Title = "Developer items",
            Status = present.Count > 0 ? ReadinessStatus.Warning : ReadinessStatus.Pass,
            Summary = present.Count > 0
                ? $"{present.Count} kind(s) of developer material found"
                : "No developer material detected",
            WhatItMeans =
                "SSH keys, Git configuration, .env files, WSL distributions and package caches are not backed up " +
                "by CTRL+Z. Their contents are never read or shown.",
            SuggestedAction = present.Count > 0
                ? "Copy ~/.ssh and your .gitconfig somewhere safe, and export any WSL distribution you still need."
                : string.Empty,
            Details = present
                .Select(item => item.Count is { } count && count > 0
                    ? $"{item.Label}: {count}"
                    : item.Label)
                .ToList()
        };
    }

    private IEnumerable<ReadinessCheck> BuildBrowserChecks(
        ReadinessRequest request,
        CancellationToken cancellationToken,
        DateTimeOffset now)
    {
        if (request.Browsers.Count == 0)
        {
            yield return new ReadinessCheck
            {
                Id = "browsers",
                Title = "Browsers",
                Status = ReadinessStatus.Pass,
                Summary = "No browsers detected",
                WhatItMeans = "CTRL+Z backs up browser profiles separately on the Browsers page."
            };
            yield break;
        }

        var newest = BrowserBackupInventory.NewestPerBrowser(
            _backups.Scan(request.BackupFolder, cancellationToken));

        foreach (var browser in request.Browsers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var backup = FindBackup(newest, browser.Name);
            var profiles = browser.ProfileCount == 1 ? "1 profile" : $"{browser.ProfileCount} profiles";

            if (backup is null)
            {
                yield return new ReadinessCheck
                {
                    Id = "browser." + Slug(browser.Name),
                    Title = browser.Name,
                    Status = ReadinessStatus.Warning,
                    Summary = "No CTRL+Z backup found",
                    WhatItMeans = "Bookmarks, history and profile settings are lost on a wipe unless they sync to a browser account.",
                    SuggestedAction = "Create a backup on the Browsers page before wiping.",
                    Details = [$"{profiles} detected", "No matching archive in the backup folder."]
                };
                continue;
            }

            var age = BrowserBackupInventory.AgeInDays(backup.CreatedUtc, now);
            var stale = BrowserBackupInventory.IsStale(backup.CreatedUtc, now, request.BackupWarningDays);

            yield return new ReadinessCheck
            {
                Id = "browser." + Slug(browser.Name),
                Title = browser.Name,
                Status = stale ? ReadinessStatus.Warning : ReadinessStatus.Pass,
                Summary = stale
                    ? $"Backup is {DescribeAge(now - backup.CreatedUtc)} old"
                    : $"Backed up {DescribeAge(now - backup.CreatedUtc)}",
                WhatItMeans =
                    "Browser profiles are backed up separately from app lists; bookmarks and history added since " +
                    "the archive was made are not in it.",
                SuggestedAction = stale
                    ? $"Make a fresh backup (the warning threshold is {request.BackupWarningDays} days)."
                    : string.Empty,
                Details =
                [
                    $"{profiles} detected",
                    $"Newest archive: {Path.GetFileName(backup.ArchivePath)}",
                    $"Age: {age} day{(age == 1 ? string.Empty : "s")}"
                ]
            };
        }
    }

    private static ReadinessCheck BuildProfileCheck(ReadinessRequest request, DateTimeOffset now)
    {
        if (request.ProfileUpdatedUtc is not { } updated)
        {
            return new ReadinessCheck
            {
                Id = "profile",
                Title = "App profile",
                Status = ReadinessStatus.Warning,
                Summary = "No saved profile yet",
                WhatItMeans = "Without a profile, CTRL+Z cannot rebuild your app list after the wipe.",
                SuggestedAction = "Scan this PC and save your apps in My Apps before wiping."
            };
        }

        var details = new List<string>
        {
            $"Last updated {DescribeAge(now - updated)}",
            request.ProfileAppCount is { } count ? $"{count} app{(count == 1 ? string.Empty : "s")} saved" : string.Empty
        };
        details.RemoveAll(string.IsNullOrWhiteSpace);

        var drift = request.Drift;
        var status = ReadinessStatus.Pass;
        var summary = "Profile is up to date";

        if (drift is not null)
        {
            details.Add($"Drift: {drift.Missing} missing, {drift.Extra} extra, {drift.Outdated} outdated");
            if (drift.Missing > 0 || drift.Extra > 0)
            {
                status = ReadinessStatus.Warning;
                summary = $"{drift.Missing} saved app{(drift.Missing == 1 ? " is" : "s are")} missing, " +
                          $"{drift.Extra} installed app{(drift.Extra == 1 ? " is" : "s are")} not saved";
            }
        }

        return new ReadinessCheck
        {
            Id = "profile",
            Title = "App profile",
            Status = status,
            Summary = summary,
            WhatItMeans =
                "The profile is the list CTRL+Z rebuilds from. Apps installed after the last save are not in it.",
            SuggestedAction = status == ReadinessStatus.Warning
                ? "Open drift detection from My Apps to review what changed."
                : string.Empty,
            Details = details,
            LinksToDrift = true
        };
    }

    private static BrowserBackupInfo? FindBackup(IReadOnlyList<BrowserBackupInfo> backups, string browserName)
    {
        var exact = backups.FirstOrDefault(backup =>
            string.Equals(backup.BrowserName, browserName, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        return backups.FirstOrDefault(backup =>
            backup.BrowserName.Contains(browserName, StringComparison.OrdinalIgnoreCase) ||
            browserName.Contains(backup.BrowserName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsInsideAnotherFolder(string path, string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string DescribeAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        var days = (int)age.TotalDays;
        return days switch
        {
            0 => "today",
            1 => "yesterday",
            _ => $"{days} days ago"
        };
    }

    private static string Slug(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.ToLowerInvariant())
        {
            builder.Append(char.IsLetterOrDigit(character) ? character : '-');
        }

        return builder.ToString().Trim('-');
    }
}
