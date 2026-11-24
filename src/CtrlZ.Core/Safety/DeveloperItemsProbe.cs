// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Localization;
using Microsoft.Win32;

namespace CtrlZ.Core.Safety;

/// <summary>One developer artefact worth knowing about before wiping. Presence only, never contents.</summary>
public sealed record DeveloperItem(string Id, string Label, bool Present, int? Count, string Note);

/// <summary>
/// Detects developer material that CTRL+Z does not back up: SSH keys, Git config, .env files,
/// WSL distributions and global package caches.
/// <para>
/// It deliberately never opens a key or a .env file. It reports **counts and locations only**
/// so that private material and project names never reach the UI, the log or a report.
/// </para>
/// </summary>
public sealed class DeveloperItemsProbe
{
    private const int MaxSearchDepth = 3;
    private const int MaxSearchFolders = 400;

    private readonly Func<int> _wslCount;

    public DeveloperItemsProbe(Func<int>? wslDistributionCount = null)
    {
        _wslCount = wslDistributionCount ?? ReadWslDistributionCount;
    }

    public IReadOnlyList<DeveloperItem> Probe(SafetyEnvironment env, CancellationToken cancellationToken = default)
    {
        var items = new List<DeveloperItem>();

        // SSH keys: count files under .ssh, never read them and never echo their names.
        var sshFolder = Combine(env.HomeDirectory, ".ssh");
        var sshCount = CountEntries(sshFolder, cancellationToken);
        items.Add(new DeveloperItem(
            "ssh",
            CoreText.Get("DeveloperItems_SshKeys"),
            sshCount is > 0,
            sshCount,
            CoreText.Get("DeveloperItems_SshKeysDetail")));

        var gitConfig = Combine(env.HomeDirectory, ".gitconfig");
        var hasGitConfig = SafeFileExists(gitConfig);
        items.Add(new DeveloperItem(
            "gitconfig",
            CoreText.Get("DeveloperItems_GitConfig"),
            hasGitConfig,
            hasGitConfig ? 1 : 0,
            CoreText.Get("DeveloperItems_GitConfigDetail")));

        var envFiles = CountEnvFiles(env, cancellationToken);
        items.Add(new DeveloperItem(
            "env-files",
            CoreText.Get("DeveloperItems_EnvFiles"),
            envFiles > 0,
            envFiles,
            CoreText.Get("DeveloperItems_EnvFilesDetail")));

        var wsl = SafeWslCount();
        items.Add(new DeveloperItem(
            "wsl",
            CoreText.Get("DeveloperItems_Wsl"),
            wsl > 0,
            wsl,
            CoreText.Get("DeveloperItems_WslDetail")));

        var packageRoots = new (string Label, string Path)[]
        {
            (CoreText.Get("DeveloperItems_NpmPackages"), Combine(env.AppData, "npm", "node_modules")),
            (CoreText.Get("DeveloperItems_NuGetCache"), Combine(env.HomeDirectory, ".nuget", "packages")),
            (CoreText.Get("DeveloperItems_CargoRegistry"), Combine(env.HomeDirectory, ".cargo", "registry")),
            (CoreText.Get("DeveloperItems_GradleCache"), Combine(env.HomeDirectory, ".gradle", "caches")),
            (CoreText.Get("DeveloperItems_MavenRepository"), Combine(env.HomeDirectory, ".m2", "repository"))
        };

        var detectedRoots = packageRoots
            .Where(root => SafeDirectoryExists(root.Path))
            .Select(root => root.Label)
            .ToList();

        items.Add(new DeveloperItem(
            "packages",
            CoreText.Get("DeveloperItems_PackageCaches"),
            detectedRoots.Count > 0,
            detectedRoots.Count,
            detectedRoots.Count == 0
                ? CoreText.Get("DeveloperItems_NoPackageCaches")
                : CoreText.Format("DeveloperItems_PackageCachesDetected", string.Join(", ", detectedRoots))));

        return items;
    }

    private int CountEnvFiles(SafetyEnvironment env, CancellationToken cancellationToken)
    {
        var roots = new[]
        {
            Combine(env.HomeDirectory, "source", "repos"),
            Combine(env.HomeDirectory, "Projects"),
            Combine(env.HomeDirectory, "dev"),
            Combine(env.HomeDirectory, "repos"),
            env.Documents,
            Combine(env.HomeDirectory, "Desktop")
        };

        var found = 0;
        var visited = 0;
        var pending = new Queue<(string Path, int Depth)>();
        // Several roots overlap (Documents can contain Projects), so each folder is visited once.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            if (SafeDirectoryExists(root))
            {
                pending.Enqueue((root, 0));
            }
        }

        while (pending.Count > 0 && visited < MaxSearchFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (path, depth) = pending.Dequeue();

            string canonical;
            try
            {
                canonical = Path.GetFullPath(path);
            }
            catch
            {
                continue;
            }

            if (!seen.Add(canonical))
            {
                continue;
            }

            visited++;

            string[] files;
            string[] folders;
            try
            {
                files = Directory.GetFiles(path);
                folders = Directory.GetDirectories(path);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (name.Equals(".env", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".env", StringComparison.OrdinalIgnoreCase))
                {
                    found++;
                }
            }

            if (depth + 1 > MaxSearchDepth)
            {
                continue;
            }

            foreach (var folder in folders)
            {
                // Skipping reparse points keeps a junction loop from exploding the walk.
                if (IsReparsePoint(folder))
                {
                    continue;
                }

                pending.Enqueue((folder, depth + 1));
            }
        }

        return found;
    }

    private int SafeWslCount()
    {
        try
        {
            return Math.Max(0, _wslCount());
        }
        catch
        {
            return 0;
        }
    }

    private static int CountEntries(string folder, CancellationToken cancellationToken)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                return 0;
            }

            var count = 0;
            foreach (var _ in Directory.EnumerateFileSystemEntries(folder))
            {
                cancellationToken.ThrowIfCancellationRequested();
                count++;
            }

            return count;
        }
        catch
        {
            return 0;
        }
    }

    private static bool SafeFileExists(string path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static bool SafeDirectoryExists(string path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true;
        }
    }

    private static string Combine(string root, params string[] parts)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return string.Empty;
        }

        try
        {
            return Path.Combine(new[] { root }.Concat(parts).ToArray());
        }
        catch
        {
            return string.Empty;
        }
    }

    private static int ReadWslDistributionCount()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
            return key?.SubKeyCount ?? 0;
        }
        catch
        {
            return 0;
        }
    }
}
