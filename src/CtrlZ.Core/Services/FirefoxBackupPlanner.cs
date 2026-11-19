// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Services;

/// <summary>
/// Firefox-family backup plan. The archive uses the same "User Data/&lt;profile&gt;/..."
/// layout as Chromium, so the restore pipeline can share path validation and new-profile
/// handling.
/// </summary>
public static class FirefoxBackupPlanner
{
    public static BrowserBackupPlan Build(
        DetectedBrowser browser,
        IReadOnlyList<BrowserProfile> selectedProfiles)
    {
        if (string.IsNullOrWhiteSpace(browser.UserDataPath))
        {
            throw new BrowserBackupException("The Firefox profile root is not configured.");
        }

        var userData = browser.UserDataPath;
        var entries = new List<BrowserPlanEntry>();
        var warnings = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string entryPath, string sourcePath)
        {
            if (!File.Exists(sourcePath) || FirefoxDataRules.IsForbiddenFile(Path.GetFileName(sourcePath)))
            {
                return;
            }

            var normalized = entryPath.Replace('\\', '/');
            if (!used.Add(normalized))
            {
                return;
            }

            long size;
            try { size = new FileInfo(sourcePath).Length; }
            catch { size = 0; }
            entries.Add(new BrowserPlanEntry(normalized, sourcePath, BrowserPlanEntryKind.CopyFile, size));
        }

        foreach (var profile in selectedProfiles)
        {
            var directory = string.IsNullOrWhiteSpace(profile.DirectoryPath)
                ? Path.Combine(userData, profile.DirectoryName)
                : profile.DirectoryPath;
            if (!Directory.Exists(directory))
            {
                warnings.Add($"Profile folder '{profile.DisplayName}' no longer exists.");
                continue;
            }

            if (!FileUtil.IsUnderDirectory(directory, userData) && !profile.IsFlat)
            {
                warnings.Add($"Skipped profile outside the Firefox root: {profile.DirectoryName}");
                continue;
            }

            var prefix = profile.IsFlat ? "User Data" : $"User Data/{profile.DirectoryName}";

            foreach (var file in FirefoxDataRules.ProfileFiles)
            {
                Add($"{prefix}/{file}", Path.Combine(directory, file));
            }

            foreach (var subdirectory in FirefoxDataRules.ProfileDirectories)
            {
                var sourceDirectory = Path.Combine(directory, subdirectory);
                if (!Directory.Exists(sourceDirectory))
                {
                    continue;
                }

                foreach (var file in EnumerateFiles(sourceDirectory))
                {
                    var relative = Path.GetRelativePath(directory, file);
                    Add($"{prefix}/{relative}", file);
                }
            }

            foreach (var storageRoot in new[] { "storage/default", "storage/permanent" })
            {
                var root = Path.Combine(directory, storageRoot.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (var extensionDirectory in Directory.EnumerateDirectories(root)
                             .Where(path => Path.GetFileName(path).StartsWith("moz-extension", StringComparison.OrdinalIgnoreCase)))
                {
                    foreach (var file in EnumerateFiles(extensionDirectory))
                    {
                        var relative = Path.GetRelativePath(directory, file);
                        Add($"{prefix}/{relative}", file);
                    }
                }
            }

            var storage = Path.Combine(directory, "storage");
            if (Directory.Exists(storage))
            {
                // Session storage may contain tab state; include only small leveldb files
                // for the profile itself, never site caches or temporary storage.
                foreach (var file in EnumerateFiles(storage, maxDepth: 1)
                             .Where(path => Path.GetFileName(path).StartsWith("ls", StringComparison.OrdinalIgnoreCase) ||
                                            Path.GetFileName(path).StartsWith("idb", StringComparison.OrdinalIgnoreCase)))
                {
                    var relative = Path.GetRelativePath(directory, file);
                    Add($"{prefix}/{relative}", file);
                }
            }
        }

        if (entries.Count == 0)
        {
            warnings.Add("No Firefox backup-eligible files were found in the selected profiles.");
        }

        return new BrowserBackupPlan
        {
            Browser = browser,
            Profiles = selectedProfiles,
            Entries = entries,
            Warnings = warnings
        };
    }

    private static IEnumerable<string> EnumerateFiles(string root, int? maxDepth = null)
    {
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (directory, depth) = stack.Pop();
            string[] files;
            string[] subdirectories;
            try
            {
                files = Directory.GetFiles(directory);
                subdirectories = maxDepth is not null && depth >= maxDepth
                    ? Array.Empty<string>()
                    : Directory.GetDirectories(directory);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                if (!FirefoxDataRules.IsForbiddenFile(Path.GetFileName(file)))
                {
                    yield return file;
                }
            }

            foreach (var subdirectory in subdirectories)
            {
                var name = Path.GetFileName(subdirectory);
                if (FirefoxDataRules.IsExcludedDirectory(name))
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

                stack.Push((subdirectory, depth + 1));
            }
        }
    }
}
