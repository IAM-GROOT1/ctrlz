using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Services;

public enum BrowserPlanEntryKind
{
    CopyFile,
    LocalState,
    WebData
}

public sealed record BrowserPlanEntry(string EntryPath, string SourcePath, BrowserPlanEntryKind Kind, long Size);

public sealed class BrowserBackupPlan
{
    public required DetectedBrowser Browser { get; init; }
    public required IReadOnlyList<BrowserProfile> Profiles { get; init; }
    public required IReadOnlyList<BrowserPlanEntry> Entries { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public long TotalBytes => Entries.Sum(e => e.Size);
    public int FileCount => Entries.Count;
}

/// <summary>
/// Builds the exact list of files a backup will read. Keeping planning separate from
/// compression makes the sensitive-file exclusions directly unit testable.
/// </summary>
public static class BrowserBackupPlanner
{
    public static BrowserBackupPlan Build(
        DetectedBrowser browser,
        IReadOnlyList<BrowserProfile> selectedProfiles)
    {
        if (string.IsNullOrWhiteSpace(browser.UserDataPath))
        {
            throw new BrowserBackupException("The browser's User Data folder is not configured.");
        }

        var userData = browser.UserDataPath;
        var entries = new List<BrowserPlanEntry>();
        var warnings = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string entryPath, string sourcePath, BrowserPlanEntryKind kind)
        {
            if (!File.Exists(sourcePath))
            {
                return;
            }

            var normalizedEntry = entryPath.Replace('\\', '/');
            if (!used.Add(normalizedEntry))
            {
                return;
            }

            long size;
            try { size = new FileInfo(sourcePath).Length; }
            catch { size = 0; }
            entries.Add(new BrowserPlanEntry(normalizedEntry, sourcePath, kind, size));
        }

        var localState = Path.Combine(userData, "Local State");
        if (File.Exists(localState))
        {
            Add("User Data/Local State", localState, BrowserPlanEntryKind.LocalState);
        }
        else
        {
            warnings.Add("Local State was not found; browser-level flags cannot be backed up.");
        }

        foreach (var profile in selectedProfiles)
        {
            var profileDirectory = profile.IsFlat
                ? userData
                : Path.Combine(userData, profile.DirectoryName);
            if (!Directory.Exists(profileDirectory))
            {
                warnings.Add($"Profile folder '{profile.DirectoryName}' no longer exists.");
                continue;
            }

            if (!profile.IsFlat && !FileUtil.IsUnderDirectory(profileDirectory, userData))
            {
                warnings.Add($"Skipped profile outside the User Data folder: {profile.DirectoryName}");
                continue;
            }

            var prefix = profile.IsFlat
                ? "User Data"
                : $"User Data/{profile.DirectoryName}";

            foreach (var fileName in BrowserDataRules.ProfileFiles)
            {
                var source = Path.Combine(profileDirectory, fileName);
                if (!File.Exists(source) || BrowserDataRules.IsForbiddenFile(fileName))
                {
                    continue;
                }

                if (fileName.Equals("Web Data", StringComparison.OrdinalIgnoreCase))
                {
                    Add($"{prefix}/Web Data", source, BrowserPlanEntryKind.WebData);
                }
                else
                {
                    Add($"{prefix}/{fileName}", source, BrowserPlanEntryKind.CopyFile);
                }
            }

            foreach (var directoryName in BrowserDataRules.ProfileDirectories)
            {
                if (BrowserDataRules.IsExcludedDirectory(directoryName))
                {
                    continue;
                }

                var sourceDirectory = Path.Combine(profileDirectory, directoryName);
                if (!Directory.Exists(sourceDirectory))
                {
                    continue;
                }

                foreach (var file in EnumerateSafeFiles(sourceDirectory))
                {
                    var relative = Path.GetRelativePath(profileDirectory, file);
                    Add($"{prefix}/{relative}", file, BrowserPlanEntryKind.CopyFile);
                }
            }
        }

        if (entries.Count == 0)
        {
            warnings.Add("No backup-eligible files were found in the selected profiles.");
        }

        return new BrowserBackupPlan
        {
            Browser = browser,
            Profiles = selectedProfiles,
            Entries = entries,
            Warnings = warnings
        };
    }

    public static BrowserBackupEstimate Estimate(BrowserBackupPlan plan)
    {
        return new BrowserBackupEstimate(
            plan.TotalBytes,
            plan.FileCount,
            BrowserDataRules.ExcludedDirectoryNames
                .Concat(BrowserDataRules.ForbiddenFileNames)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray(),
            plan.Warnings);
    }

    private static IEnumerable<string> EnumerateSafeFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var directory = stack.Pop();
            string[] files;
            string[] subdirectories;
            try
            {
                files = Directory.GetFiles(directory);
                subdirectories = Directory.GetDirectories(directory);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                if (!BrowserDataRules.IsForbiddenFile(Path.GetFileName(file)))
                {
                    yield return file;
                }
            }

            foreach (var subdirectory in subdirectories)
            {
                var name = Path.GetFileName(subdirectory);
                if (BrowserDataRules.IsExcludedDirectory(name))
                {
                    continue;
                }

                try
                {
                    if ((File.GetAttributes(subdirectory) & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                stack.Push(subdirectory);
            }
        }
    }
}
