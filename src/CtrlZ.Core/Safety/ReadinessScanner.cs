// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Localization;
using System.Text;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Logging;

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
    private readonly IAppLog? _log;

    public ReadinessScanner(
        FolderUsageScanner? folders = null,
        DeveloperItemsProbe? developer = null,
        GameLibraryProbe? games = null,
        BrowserBackupInventory? backups = null,
        IAppLog? log = null)
    {
        _folders = folders ?? new FolderUsageScanner();
        _developer = developer ?? new DeveloperItemsProbe();
        _games = games ?? new GameLibraryProbe();
        _backups = backups ?? new BrowserBackupInventory();
        _log = log;
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
        var unreadableFolders = 0;

        foreach (var folder in request.UserFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(folder.Label);

            var usage = _folders.Scan(folder.Path, folder.Options ?? request.FolderOptions, cancellationToken);
            if (usage.AccessDenied)
            {
                unreadableFolders++;
            }

            checks.Add(BuildFolderCheck(folder, usage, request, now));
        }

        Report(CoreText.Get("ReadinessScanner_GamesAndLaunchers"));
        checks.Add(BuildGamesCheck(request, cancellationToken));

        Report(CoreText.Get("ReadinessScanner_DeveloperItems"));
        checks.Add(BuildDeveloperCheck(request, cancellationToken));

        Report(CoreText.Get("ReadinessScanner_Browsers"));
        checks.AddRange(BuildBrowserChecks(request, cancellationToken, now));

        Report(CoreText.Get("ReadinessScanner_Profile"));
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
                Summary = CoreText.Get("ReadinessScanner_ConfirmThisYourselfCTRLZ"),
                WhatItMeans = reminder.WhatItMeans,
                SuggestedAction = reminder.SuggestedAction
            });
        }

        var report = new ReadinessReport { GeneratedUtc = now, Checks = checks };

        // Counts only: the log must never learn a personal folder or file name.
        _log?.Write(LogLevel.Info, SafetyDiagnostics.Summary(report, unreadableFolders));

        return report;
    }

    private static ReadinessCheck BuildFolderCheck(
        UserFolder folder,
        FolderUsage usage,
        ReadinessRequest request,
        DateTimeOffset now)
    {
        var id = "folder." + Slug(folder.Id ?? folder.Label);
        const string meaning =
            "These files exist only on this PC. A clean install removes them unless a copy exists somewhere else.";

        if (!usage.Exists)
        {
            return new ReadinessCheck
            {
                Id = id,
                Title = folder.Label,
                Status = ReadinessStatus.Pass,
                Summary = CoreText.Get("ReadinessScanner_NotPresentOnThisPC"),
                WhatItMeans = meaning
            };
        }

        var size = FileUtil.FormatBytes(usage.TotalBytes);
        var covered = IsInsideAnotherFolder(usage.Path, request.BackupFolder);
        var details = new List<string> { CoreText.Format("ReadinessScanner_Files", usage.FileCount, size) };

        if (usage.LastWriteUtc is { } last)
        {
            details.Add(CoreText.Format("ReadinessScanner_LastChange", DescribeAge(now - last)));
        }

        if (usage.Truncated)
        {
            details.Add(CoreText.Get("ReadinessScanner_ScanStoppedAtALimit"));
        }

        if (usage.AccessDenied)
        {
            details.Add(CoreText.Get("ReadinessScanner_SomeFoldersCouldNotBe"));
        }

        details.Add(covered
            ? CoreText.Get("ReadinessScanner_InsideYourCTRLZBackup")
            : CoreText.Get("ReadinessScanner_NoCTRLZBackupCovers"));

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
                ? CoreText.Get("ReadinessScanner_CoveredByYourBackupFolder")
                : flagged
                    ? (large && recent ? CoreText.Get("ReadinessScanner_LargeAndRecentlyChangedNo")
                        : large ? CoreText.Get("ReadinessScanner_LargeNoBackup")
                        : CoreText.Get("ReadinessScanner_RecentlyChangedNoBackup"))
                    : CoreText.Format("ReadinessScanner_NoBackupButQuiet", size),
            WhatItMeans = meaning,
            SuggestedAction = flagged
                ? CoreText.Get("ReadinessScanner_CopyThisFolderToExternal")
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
            Title = CoreText.Get("ReadinessScanner_GamesAndLaunchers"),
            Status = detected.Count > 0 ? ReadinessStatus.Warning : ReadinessStatus.Pass,
            Summary = detected.Count > 0
                ? string.Join(", ", detected.Select(library => library.Name)) + " detected"
                : CoreText.Get("ReadinessScanner_NoGameLaunchersDetected"),
            WhatItMeans =
                CoreText.Get("ReadinessScanner_LauncherLibrariesAndGameSaves") +
                CoreText.Get("ReadinessScanner_AreOnlySafeOnceThe"),
            SuggestedAction = detected.Count > 0
                ? CoreText.Get("ReadinessScanner_CheckEachLauncherReportsIts")
                : string.Empty,
            Details = detected
                .Select(library =>
                {
                    var saves = library.SavePaths.Count == 0
                        ? CoreText.Get("ReadinessScanner_NoSaveFoldersFound")
                        : CoreText.Plural("ReadinessScanner_SaveFolders", library.SavePaths.Count);
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
            Title = CoreText.Get("ReadinessScanner_DeveloperItems"),
            Status = present.Count > 0 ? ReadinessStatus.Warning : ReadinessStatus.Pass,
            Summary = present.Count > 0
                ? CoreText.Format("ReadinessScanner_KindSOfDeveloperMaterial", present.Count)
                : CoreText.Get("ReadinessScanner_NoDeveloperMaterialDetected"),
            WhatItMeans =
                CoreText.Get("ReadinessScanner_SSHKeysGitConfigurationEnv") +
                CoreText.Get("ReadinessScanner_ByCTRLZTheirContents"),
            SuggestedAction = present.Count > 0
                ? CoreText.Get("ReadinessScanner_CopySshAndYourGitconfig")
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
                Title = CoreText.Get("ReadinessScanner_Browsers"),
                Status = ReadinessStatus.Pass,
                Summary = CoreText.Get("ReadinessScanner_NoBrowsersDetected"),
                WhatItMeans = CoreText.Get("ReadinessScanner_CTRLZBacksUpBrowser")
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
                    Summary = CoreText.Get("ReadinessScanner_NoCTRLZBackupFound"),
                    WhatItMeans = CoreText.Get("ReadinessScanner_BookmarksHistoryAndProfileSettings"),
                    SuggestedAction = CoreText.Get("ReadinessScanner_CreateABackupOnThe"),
                    Details = [$"{profiles} detected", CoreText.Get("ReadinessScanner_NoMatchingArchiveInThe")]
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
                    ? CoreText.Format("ReadinessScanner_BackupIsOld", DescribeAge(now - backup.CreatedUtc))
                    : CoreText.Format("ReadinessScanner_BackedUp", DescribeAge(now - backup.CreatedUtc)),
                WhatItMeans =
                    CoreText.Get("ReadinessScanner_BrowserProfilesAreBackedUp") +
                    CoreText.Get("ReadinessScanner_TheArchiveWasMadeAre"),
                SuggestedAction = stale
                    ? CoreText.Format("ReadinessScanner_MakeAFreshBackupThe", request.BackupWarningDays)
                    : string.Empty,
                Details =
                [
                    $"{profiles} detected",
                    CoreText.Format("ReadinessScanner_NewestArchive", Path.GetFileName(backup.ArchivePath)),
                    CoreText.Plural("ReadinessScanner_AgeDays", age)
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
                Title = CoreText.Get("ReadinessScanner_AppProfile"),
                Status = ReadinessStatus.Warning,
                Summary = CoreText.Get("ReadinessScanner_NoSavedProfileYet"),
                WhatItMeans = CoreText.Get("ReadinessScanner_WithoutAProfileCTRLZ"),
                SuggestedAction = CoreText.Get("ReadinessScanner_ScanThisPCAndSave")
            };
        }

        var details = new List<string>
        {
            CoreText.Format("ReadinessScanner_LastUpdated", DescribeAge(now - updated)),
            request.ProfileAppCount is { } count ? CoreText.Plural("ReadinessScanner_AppsSaved", count) : string.Empty
        };
        details.RemoveAll(string.IsNullOrWhiteSpace);

        var drift = request.Drift;
        var status = ReadinessStatus.Pass;
        var summary = CoreText.Get("ReadinessScanner_ProfileIsUpToDate");

        if (drift is not null)
        {
            details.Add(CoreText.Format("ReadinessScanner_DriftMissingExtraOutdated", drift.Missing, drift.Extra, drift.Outdated));
            if (drift.Missing > 0 || drift.Extra > 0)
            {
                status = ReadinessStatus.Warning;
                summary = CoreText.Plural("ReadinessScanner_SavedAppsMissing", drift.Missing) +
                          CoreText.Plural("ReadinessScanner_InstalledAppsNotSaved", drift.Extra);
            }
        }

        return new ReadinessCheck
        {
            Id = "profile",
            Title = CoreText.Get("ReadinessScanner_AppProfile"),
            Status = status,
            Summary = summary,
            WhatItMeans =
                CoreText.Get("ReadinessScanner_TheProfileIsTheList"),
            SuggestedAction = status == ReadinessStatus.Warning
                ? CoreText.Get("ReadinessScanner_OpenDriftDetectionFromMy")
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
            _ => CoreText.Format("ReadinessScanner_DaysAgo", days)
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
