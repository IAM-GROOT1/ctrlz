// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Diagnostics;

namespace CtrlZ.Core.Safety;

/// <summary>
/// Read-only, bounded directory walker used by the readiness checks.
/// <para>
/// It only enumerates entries and reads their size and last-write time: it never creates,
/// moves, renames, writes or deletes anything. Failures are contained rather than thrown —
/// an unreadable folder sets <see cref="FolderUsage.AccessDenied"/>, and junctions are
/// skipped unless explicitly enabled so a loop cannot hang the UI.
/// </para>
/// </summary>
public sealed class FolderUsageScanner
{
    public FolderUsage Scan(
        string path,
        FolderUsageOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var limits = options ?? FolderUsageOptions.Default;

        if (string.IsNullOrWhiteSpace(path) || !SafeDirectoryExists(path))
        {
            return new FolderUsage { Path = path ?? string.Empty, Exists = false };
        }

        var stopwatch = Stopwatch.StartNew();
        long bytes = 0;
        var fileCount = 0;
        var accessDenied = false;
        var truncated = false;
        DateTimeOffset? newestWrite = null;

        var pending = new Stack<(DirectoryInfo Directory, int Depth)>();
        pending.Push((new DirectoryInfo(path), 0));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (limits.MaxDuration > TimeSpan.Zero && stopwatch.Elapsed >= limits.MaxDuration)
            {
                truncated = true;
                break;
            }

            var (directory, depth) = pending.Pop();
            var enumerator = EnumerateSafely(directory, ref accessDenied);
            if (enumerator is null)
            {
                continue;
            }

            var hitFileCap = false;
            using (enumerator)
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    FileSystemInfo entry;
                    try
                    {
                        if (!enumerator.MoveNext())
                        {
                            break;
                        }

                        entry = enumerator.Current;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        accessDenied = true;
                        break;
                    }
                    catch (IOException)
                    {
                        accessDenied = true;
                        break;
                    }

                    var written = SafeLastWrite(entry, ref accessDenied);
                    if (written is { } value && (newestWrite is null || value > newestWrite))
                    {
                        newestWrite = value;
                    }

                    if (entry is FileInfo file)
                    {
                        bytes += SafeLength(file, ref accessDenied);
                        fileCount++;

                        if (fileCount >= limits.MaxFiles)
                        {
                            truncated = true;
                            hitFileCap = true;
                            break;
                        }

                        continue;
                    }

                    if (entry is DirectoryInfo child)
                    {
                        // A junction or symlink is skipped: following one can loop forever.
                        if (!limits.FollowReparsePoints && IsReparsePoint(child))
                        {
                            continue;
                        }

                        if (depth + 1 > limits.MaxDepth)
                        {
                            truncated = true;
                            continue;
                        }

                        pending.Push((child, depth + 1));
                    }
                }
            }

            if (hitFileCap)
            {
                break;
            }
        }

        return new FolderUsage
        {
            Path = path,
            Exists = true,
            TotalBytes = bytes,
            FileCount = fileCount,
            LastWriteUtc = newestWrite,
            AccessDenied = accessDenied,
            Truncated = truncated
        };
    }

    /// <summary>Scans several folders in order, sharing one cancellation token.</summary>
    public IReadOnlyList<FolderUsage> ScanMany(
        IEnumerable<string> paths,
        FolderUsageOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<FolderUsage>();
        foreach (var path in paths)
        {
            results.Add(Scan(path, options, cancellationToken));
        }

        return results;
    }

    private static IEnumerator<FileSystemInfo>? EnumerateSafely(DirectoryInfo directory, ref bool accessDenied)
    {
        try
        {
            return directory.EnumerateFileSystemInfos().GetEnumerator();
        }
        catch (UnauthorizedAccessException)
        {
            accessDenied = true;
            return null;
        }
        catch (IOException)
        {
            accessDenied = true;
            return null;
        }
    }

    private static bool SafeDirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static DateTimeOffset? SafeLastWrite(FileSystemInfo entry, ref bool accessDenied)
    {
        try
        {
            return new DateTimeOffset(DateTime.SpecifyKind(entry.LastWriteTimeUtc, DateTimeKind.Utc));
        }
        catch (UnauthorizedAccessException)
        {
            accessDenied = true;
            return null;
        }
        catch (IOException)
        {
            accessDenied = true;
            return null;
        }
    }

    private static long SafeLength(FileInfo file, ref bool accessDenied)
    {
        try
        {
            return file.Length;
        }
        catch (UnauthorizedAccessException)
        {
            accessDenied = true;
            return 0;
        }
        catch (IOException)
        {
            accessDenied = true;
            return 0;
        }
    }

    private static bool IsReparsePoint(FileSystemInfo entry)
    {
        try
        {
            return (entry.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (UnauthorizedAccessException)
        {
            // Unknown attributes: do not descend, so a possible loop cannot be entered.
            return true;
        }
        catch (IOException)
        {
            return true;
        }
    }
}
