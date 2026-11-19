// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Diagnostics;
using System.Security.Cryptography;
using CtrlZ.Core.Safety;

namespace CtrlZ.Core.Tests;

public class FolderUsageScannerTests
{
    private static readonly FolderUsageOptions Unlimited = new()
    {
        MaxDepth = 64,
        MaxFiles = 1_000_000,
        MaxDuration = TimeSpan.Zero,
        FollowReparsePoints = false
    };

    [Fact]
    public void Missing_folder_is_reported_as_not_existing()
    {
        var directory = new TestDirectory();
        try
        {
            var usage = new FolderUsageScanner().Scan(directory.PathOf("nope"));

            Assert.False(usage.Exists);
            Assert.Equal(0, usage.FileCount);
            Assert.Equal(0, usage.TotalBytes);
        }
        finally
        {
            directory.Dispose();
        }
    }

    [Fact]
    public void A_path_that_is_a_file_is_not_treated_as_a_folder()
    {
        var directory = new TestDirectory();
        try
        {
            var file = directory.Write("single.txt", "hello");

            var usage = new FolderUsageScanner().Scan(file);

            Assert.False(usage.Exists);
        }
        finally
        {
            directory.Dispose();
        }
    }

    [Fact]
    public void Counts_files_and_bytes_across_nested_folders()
    {
        var directory = new TestDirectory();
        try
        {
            directory.Write("one.txt", "12345");            // 5 bytes
            directory.Write("sub/two.txt", "1234567890");   // 10 bytes
            directory.Write("sub/deeper/three.txt", "123"); // 3 bytes

            var usage = new FolderUsageScanner().Scan(directory.Root, Unlimited);

            Assert.True(usage.Exists);
            Assert.Equal(3, usage.FileCount);
            Assert.Equal(18, usage.TotalBytes);
            Assert.False(usage.Truncated);
        }
        finally
        {
            directory.Dispose();
        }
    }

    [Fact]
    public void Reports_the_newest_write_time_it_sees()
    {
        var directory = new TestDirectory();
        try
        {
            var oldFile = directory.Write("old.txt", "x");
            var newFile = directory.Write("new.txt", "y");
            File.SetLastWriteTimeUtc(oldFile, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var newest = new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(newFile, newest);

            var usage = new FolderUsageScanner().Scan(directory.Root, Unlimited);

            Assert.NotNull(usage.LastWriteUtc);
            Assert.Equal(newest, usage.LastWriteUtc!.Value.UtcDateTime);
        }
        finally
        {
            directory.Dispose();
        }
    }

    [Fact]
    public void Depth_cap_excludes_deep_files_and_marks_the_result_partial()
    {
        var directory = new TestDirectory();
        try
        {
            directory.Write("keep.txt", "12345");
            directory.Write("l1/keep2.txt", "12345");
            directory.Write("l1/l2/l3/deep.txt", "12345");

            var usage = new FolderUsageScanner().Scan(directory.Root, Unlimited with { MaxDepth = 2 });

            Assert.Equal(2, usage.FileCount);
            Assert.True(usage.Truncated);
        }
        finally
        {
            directory.Dispose();
        }
    }

    [Fact]
    public void File_cap_stops_the_walk_and_marks_the_result_partial()
    {
        var directory = new TestDirectory();
        try
        {
            for (var i = 0; i < 20; i++)
            {
                directory.Write($"f{i}.txt", "x");
            }

            var usage = new FolderUsageScanner().Scan(directory.Root, Unlimited with { MaxFiles = 5 });

            Assert.Equal(5, usage.FileCount);
            Assert.True(usage.Truncated);
        }
        finally
        {
            directory.Dispose();
        }
    }

    [Fact]
    public void Time_budget_stops_the_walk_and_marks_the_result_partial()
    {
        var directory = new TestDirectory();
        try
        {
            directory.Write("a.txt", "x");

            // One tick: the budget is exhausted before the first entry is examined.
            var usage = new FolderUsageScanner().Scan(
                directory.Root,
                Unlimited with { MaxDuration = TimeSpan.FromTicks(1) });

            Assert.True(usage.Truncated);
        }
        finally
        {
            directory.Dispose();
        }
    }

    [Fact]
    public void Cancellation_is_honoured()
    {
        var directory = new TestDirectory();
        try
        {
            directory.Write("a.txt", "x");
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.Throws<OperationCanceledException>(
                () => new FolderUsageScanner().Scan(directory.Root, Unlimited, cts.Token));
        }
        finally
        {
            directory.Dispose();
        }
    }

    [Fact]
    public void Scanning_a_junction_loop_terminates_and_does_not_double_count()
    {
        var directory = new TestDirectory();
        var loopPath = directory.PathOf("sub", "loop");
        try
        {
            directory.Write("a.txt", "12345");
            directory.Write("sub/b.txt", "12345");

            if (!TryCreateJunction(loopPath, directory.Root))
            {
                // Junctions need NTFS; nothing to assert on other file systems.
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            var usage = new FolderUsageScanner().Scan(
                directory.Root,
                Unlimited with { MaxDuration = TimeSpan.FromSeconds(20) });
            stopwatch.Stop();

            Assert.Equal(2, usage.FileCount);
            Assert.Equal(10, usage.TotalBytes);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), "a junction loop must not slow the scan down");
        }
        finally
        {
            TryDeleteJunction(loopPath);
            directory.Dispose();
        }
    }

    [Fact]
    public void Scanning_never_modifies_the_tree()
    {
        var directory = new TestDirectory();
        try
        {
            directory.Write("a.txt", "alpha");
            directory.Write("sub/b.txt", "bravo");
            directory.Write("sub/deeper/c.txt", "charlie");

            var before = Snapshot(directory.Root);

            var usage = new FolderUsageScanner().Scan(
                directory.Root,
                Unlimited with { MaxFiles = 2 });   // force an early stop as well

            var after = Snapshot(directory.Root);

            Assert.True(usage.Exists);
            Assert.Equal(before, after);
        }
        finally
        {
            directory.Dispose();
        }
    }

    [Fact]
    public void ScanMany_reports_each_folder()
    {
        var directory = new TestDirectory();
        try
        {
            var a = directory.CreateDirectory("a");
            directory.Write("a/one.txt", "123");
            var b = directory.CreateDirectory("b");

            var results = new FolderUsageScanner().ScanMany([a, b, directory.PathOf("missing")], Unlimited);

            Assert.Equal(3, results.Count);
            Assert.Equal(1, results[0].FileCount);
            Assert.Equal(0, results[1].FileCount);
            Assert.False(results[2].Exists);
        }
        finally
        {
            directory.Dispose();
        }
    }

    // ---- helpers ------------------------------------------------------------------

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
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            entries.Add($"file|{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{hash}");
        }

        return entries;
    }

    private static bool TryCreateJunction(string linkPath, string targetPath)
    {
        try
        {
            var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(start);
            process?.WaitForExit(10_000);
            return Directory.Exists(linkPath);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteJunction(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                // Delete the link only; never recurse through it.
                Directory.Delete(path);
            }
        }
        catch
        {
            // Leaving a junction behind in a temp folder is harmless.
        }
    }
}
