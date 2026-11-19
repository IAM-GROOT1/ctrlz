// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Globalization;
using System.Text.RegularExpressions;

namespace CtrlZ.Core.Safety;

/// <summary>One CTRL+Z browser archive found in the configured backup folder.</summary>
public sealed record BrowserBackupInfo(string BrowserName, string ArchivePath, DateTimeOffset CreatedUtc);

/// <summary>
/// Finds existing CTRL+Z browser archives and works out when each browser was last backed
/// up. Backups are named <c>CTRLZ-&lt;browser&gt;-&lt;yyyyMMdd-HHmm&gt;.zip</c>, so the age can be
/// read from the name without opening the archive — important because archives can be
/// password protected and because opening them would be far slower.
/// </summary>
public sealed partial class BrowserBackupInventory
{
    public const string ArchivePrefix = "CTRLZ-";
    public const string ArchiveExtension = ".zip";

    [GeneratedRegex(@"^CTRLZ-(?<browser>.+)-(?<stamp>\d{8}-\d{4})(?:-\d+)?\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex ArchiveName();

    /// <summary>
    /// Lists archives in <paramref name="backupFolder"/>. A missing or unset folder yields an
    /// empty list rather than an error: the user may simply never have made a backup.
    /// </summary>
    public IReadOnlyList<BrowserBackupInfo> Scan(string? backupFolder, CancellationToken cancellationToken = default)
    {
        var results = new List<BrowserBackupInfo>();
        if (string.IsNullOrWhiteSpace(backupFolder))
        {
            return results;
        }

        string[] files;
        try
        {
            if (!Directory.Exists(backupFolder))
            {
                return results;
            }

            files = Directory.GetFiles(backupFolder, ArchivePrefix + "*" + ArchiveExtension, SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return results;
        }

        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (TryRead(path, out var info))
            {
                results.Add(info);
            }
        }

        return results
            .OrderByDescending(item => item.CreatedUtc)
            .ToList();
    }

    /// <summary>The most recent archive per browser, keyed case-insensitively by browser name.</summary>
    public static IReadOnlyList<BrowserBackupInfo> NewestPerBrowser(IEnumerable<BrowserBackupInfo> backups) =>
        backups
            .GroupBy(item => item.BrowserName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.CreatedUtc).First())
            .OrderBy(item => item.BrowserName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Whole days between the backup and <paramref name="now"/>; never negative.</summary>
    public static int AgeInDays(DateTimeOffset createdUtc, DateTimeOffset now)
    {
        var age = now - createdUtc;
        return age <= TimeSpan.Zero ? 0 : (int)age.TotalDays;
    }

    /// <summary>True when a backup is older than the warning threshold (0 or less disables it).</summary>
    public static bool IsStale(DateTimeOffset createdUtc, DateTimeOffset now, int warningDays) =>
        warningDays > 0 && AgeInDays(createdUtc, now) >= warningDays;

    private static bool TryRead(string path, out BrowserBackupInfo info)
    {
        info = default!;
        var name = Path.GetFileName(path);

        var match = ArchiveName().Match(name);
        if (match.Success &&
            DateTime.TryParseExact(
                match.Groups["stamp"].Value,
                "yyyyMMdd-HHmm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out var local))
        {
            info = new BrowserBackupInfo(
                match.Groups["browser"].Value.Trim(),
                path,
                new DateTimeOffset(local));
            return true;
        }

        // Prefix matches but the stamp does not parse: fall back to the file's own timestamp
        // rather than silently dropping what may still be a real backup.
        if (!name.StartsWith(ArchivePrefix, StringComparison.OrdinalIgnoreCase) ||
            !name.EndsWith(ArchiveExtension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var modified = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            var browser = name[ArchivePrefix.Length..^ArchiveExtension.Length];
            info = new BrowserBackupInfo(browser.Trim(), path, modified);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
