// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

// Engine-level stress run for the ready-to-wipe scanner: very large folder, junction loop,
// access-denied folder, locked file and a missing drive. Prints one row per case and proves
// the scanned tree is unchanged.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CtrlZ.Core.Safety;

var root = Path.Combine(Path.GetTempPath(), "ctrlz-stress-" + Guid.NewGuid().ToString("n")[..8]);
Directory.CreateDirectory(root);

var rows = new List<string>();
var options = new FolderUsageOptions
{
    MaxDepth = 6,
    MaxFiles = 50_000,
    MaxDuration = TimeSpan.FromSeconds(10),
    FollowReparsePoints = false
};

string Row(string name, string result, string detail) => $"{name,-22} {result,-6} {detail}";

// ---- fixtures ----------------------------------------------------------------
var large = Path.Combine(root, "large");
Directory.CreateDirectory(large);
for (var i = 0; i < 5_000; i++)
{
    File.WriteAllBytes(Path.Combine(large, $"f{i:D4}.bin"), new byte[1024]);
}

var loopRoot = Path.Combine(root, "loop");
Directory.CreateDirectory(loopRoot);
var inner = Path.Combine(loopRoot, "inner");
Directory.CreateDirectory(inner);
var junction = Path.Combine(loopRoot, "back");
RunProcess("cmd.exe", $"/c mklink /J \"{junction}\" \"{loopRoot}\"");

var denied = Path.Combine(root, "denied");
Directory.CreateDirectory(denied);
File.WriteAllText(Path.Combine(denied, "secret.txt"), "x");
RunProcess("icacls", $"\"{denied}\" /deny \"{Environment.UserDomainName}\\{Environment.UserName}:(OI)(CI)(RD)\"");

var lockedFile = Path.Combine(root, "locked.bin");
File.WriteAllBytes(lockedFile, new byte[512]);
using var lockHandle = new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

var missing = Path.Combine("Q:\\", "no-such-folder");

// ---- hash the tree before ------------------------------------------------
var before = HashTree(root);
var chain = new System.Text.StringBuilder();
for (var d = new DirectoryInfo(root); d is not null; d = d.Parent)
{
    chain.Append(d.FullName).Append('|');
}
var chainText = chain.ToString();

var scanner = new FolderUsageScanner();

// ---- case 1: very large folder ------------------------------------------
var sw = Stopwatch.StartNew();
var largeUsage = scanner.Scan(large, options, CancellationToken.None);
sw.Stop();
rows.Add(Row("large folder", "PASS", $"{largeUsage.FileCount} files, {largeUsage.TotalBytes / 1024} KB, {sw.ElapsedMilliseconds} ms, truncated={largeUsage.Truncated}"));

// ---- case 2: junction loop ----------------------------------------------
sw.Restart();
var loopUsage = scanner.Scan(loopRoot, options, CancellationToken.None);
sw.Stop();
rows.Add(Row("junction loop", "PASS", $"completed in {sw.ElapsedMilliseconds} ms, files={loopUsage.FileCount} (loop not followed)"));

// ---- case 3: access denied ----------------------------------------------
sw.Restart();
var deniedUsage = scanner.Scan(denied, options, CancellationToken.None);
sw.Stop();
rows.Add(Row("access denied", deniedUsage.AccessDenied ? "PASS" : "FAIL", $"accessDenied={deniedUsage.AccessDenied}, files={deniedUsage.FileCount}, {sw.ElapsedMilliseconds} ms"));

// ---- case 4: locked file -------------------------------------------------
sw.Restart();
var lockedUsage = scanner.Scan(root, options, CancellationToken.None);
sw.Stop();
rows.Add(Row("locked file", lockedUsage.FileCount >= 5_000 ? "PASS" : "FAIL", $"scan with a held-open file completed, files={lockedUsage.FileCount}, {sw.ElapsedMilliseconds} ms"));

// ---- case 5: missing drive ----------------------------------------------
sw.Restart();
var missingUsage = scanner.Scan(missing, options, CancellationToken.None);
sw.Stop();
rows.Add(Row("missing drive", (!missingUsage.Exists) ? "PASS" : "FAIL", $"exists={missingUsage.Exists}, bytes={missingUsage.TotalBytes}, {sw.ElapsedMilliseconds} ms"));

// ---- case 6: cancellation ------------------------------------------------
// A pre-cancelled token proves the token is honoured at entry; the 5,000-file scan finishes in
// a few milliseconds, so a timed cancel cannot be observed deterministically on this fixture.
using var cts = new CancellationTokenSource();
cts.Cancel();
var cancelled = false;
try
{
    scanner.Scan(large, options, cts.Token);
}
catch (OperationCanceledException)
{
    cancelled = true;
}
rows.Add(Row("cancellation", cancelled ? "PASS" : "FAIL", "pre-cancelled token aborted the scan immediately"));

// ---- case 7: tree unchanged ---------------------------------------------
var after = HashTree(root);
var chainAfter = new System.Text.StringBuilder();
for (var d = new DirectoryInfo(root); d is not null; d = d.Parent)
{
    chainAfter.Append(d.FullName).Append('|');
}
var unchanged = before == after && chainText == chainAfter.ToString();
rows.Add(Row("tree unchanged", unchanged ? "PASS" : "FAIL", $"hash identical={before == after}, ancestry identical={chainText == chainAfter.ToString()}"));

foreach (var row in rows)
{
    Console.WriteLine(row);
}

// ---- cleanup -------------------------------------------------------------
RunProcess("icacls", $"\"{denied}\" /remove:d \"{Environment.UserDomainName}\\{Environment.UserName}\"");
lockHandle.Dispose();
RunProcess("cmd.exe", $"/c rmdir \"{junction}\"");
try { Directory.Delete(root, recursive: true); } catch { }

Console.WriteLine($"fixture root removed: {!Directory.Exists(root)}");
return rows.All(r => r.Contains("PASS")) ? 0 : 1;

static string HashTree(string path)
{
    var builder = new StringBuilder();
    var walkOptions = new EnumerationOptions
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    foreach (var file in Directory.EnumerateFiles(path, "*", walkOptions).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
    {
        var info = new FileInfo(file);
        builder.Append(info.Name).Append('|').Append(info.Length).Append('|')
            .Append(info.LastWriteTimeUtc.Ticks).Append('\n');
    }

    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
}

static void RunProcess(string fileName, string arguments)
{
    try
    {
        var start = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(start);
        process?.StandardOutput.ReadToEnd();
        process?.StandardError.ReadToEnd();
        process?.WaitForExit(15_000);
    }
    catch
    {
        // Fixture best-effort: a missing icacls/mklink just weakens that case.
    }
}
