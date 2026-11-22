// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.IO.Compression;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Models;
using Xunit;

namespace CtrlZ.Core.Tests;

public sealed class DiagnosticsBundleTests : IDisposable
{
    private const string Home = @"C:\Users\tonyx";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ctrlz-diag-" + Guid.NewGuid().ToString("n"));

    private static readonly PathPrivacy Privacy = new(
        [("%LOCALAPPDATA%", Home + @"\AppData\Local"), ("%USERPROFILE%", Home)],
        "tonyx",
        "DESKPC77");

    public DiagnosticsBundleTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static DiagnosticsInput Input() => new()
    {
        AppVersion = "1.4.0",
        DataLocation = "installed",
        WingetStatus = "winget 1.9 · ready",
        Settings = new AppSettings
        {
            IncludeNotesInReports = true,
            LastBackupFolder = Home + @"\Documents\Private Backups",
            CustomBrowserUserDataPath = @"D:\SecretBrowser"
        },
        ProfileCount = 2,
        ActiveProfile =
            new AppProfile
            {
                Name = "Tony's gaming rig",
                Apps =
                [
                    new AppEntry { Id = "a", Name = "Steam", Source = AppSource.Winget, Notes = "licence ABCD-1234" },
                    new AppEntry { Id = "b", Name = "VC++", Source = AppSource.Winget, IsAutoDependency = true },
                    new AppEntry
                    {
                        Id = "c", Name = "MyTool", Source = AppSource.LocalFile, LocalPath = @"D:\Installers\mytool.exe",
                        PostInstall = new PostInstallCommand { Executable = "secret-cmd.exe", Arguments = ["--token", "hunter2"] }
                    }
                ]
            },
        LastInstall = new InstallState
        {
            ProfileName = "Tony's gaming rig",
            IsFinished = true,
            Items =
            [
                new InstallItemState { Name = "Steam", Status = InstallStatus.Success },
                new InstallItemState { Name = "MyTool", Status = InstallStatus.Failed, Error = @"D:\Installers\mytool.exe failed" }
            ]
        },
    };

    [Fact]
    public void Summary_counts_without_names_paths_notes_or_commands()
    {
        var summary = DiagnosticsBundle.Summary(Input());

        Assert.Contains("CTRL+Z 1.4.0", summary);
        Assert.Contains("Saved profiles: 2", summary);
        Assert.Contains("Apps in the open profile: 3", summary);
        Assert.Contains("Winget: 2", summary);
        Assert.Contains("Added as dependencies: 1", summary);
        Assert.Contains("With a post-install command: 1", summary);
        Assert.Contains("With a note: 1", summary);
        Assert.Contains("Failed: 1", summary);
        Assert.Contains("Custom browser data folder: yes", summary);
        foreach (var secret in new[] { "Tony", "Steam", "MyTool", "ABCD", "secret", "hunter2", "Installers", "Private Backups", "SecretBrowser" })
        {
            Assert.False(summary.Contains(secret, StringComparison.OrdinalIgnoreCase), secret);
        }
    }

    [Theory]
    [InlineData(@"Saved C:\Users\tonyx\Documents\taxes 2026.pdf", @"Saved %USERPROFILE%\<path>")]
    [InlineData(@"Opened D:\Work\plans.docx", @"Opened <path>")]
    [InlineData(@"State %LOCALAPPDATA%\CTRL+Z\state\install-state.json", @"State %LOCALAPPDATA%\CTRL+Z\state\install-state.json")]
    [InlineData(@"Wrote C:\Users\tonyx\AppData\Local\CTRL+Z\logs\a.log", @"Wrote %LOCALAPPDATA%\CTRL+Z\logs\a.log")]
    [InlineData(@"Other %LOCALAPPDATA%\Google\Chrome\User Data", @"Other %LOCALAPPDATA%\<path>")]
    [InlineData("User tonyx on DESKPC77 started", "User <user> on <pc> started")]
    public void Log_lines_lose_paths_and_names(string line, string expected) =>
        Assert.Equal(expected, DiagnosticsBundle.CleanLogLine(line, Privacy));

    [Fact]
    public void A_kept_ctrlz_path_does_not_carry_a_later_personal_path()
    {
        var cleaned = DiagnosticsBundle.CleanLogLine(
            @"Copied %LOCALAPPDATA%\CTRL+Z\backups\x.zip to %USERPROFILE%\Desktop\diary.txt", Privacy);

        Assert.DoesNotContain("diary", cleaned);
        Assert.StartsWith(@"Copied %LOCALAPPDATA%\CTRL+Z\backups\x.zip to %USERPROFILE%\<path>", cleaned);
    }

    [Fact]
    public void Bundle_has_readme_summary_and_cleaned_recent_logs()
    {
        var logs = Path.Combine(_folder, "logs");
        Directory.CreateDirectory(logs);
        for (var day = 1; day <= 9; day++)
        {
            var file = Path.Combine(logs, $"ctrlz-2026-11-{day:00}.log");
            File.WriteAllText(file, $"day {day} user tonyx opened C:\\Users\\tonyx\\Documents\\secret.txt\n");
            File.SetLastWriteTimeUtc(file, new DateTime(2026, 11, day, 12, 0, 0, DateTimeKind.Utc));
        }

        File.WriteAllText(Path.Combine(logs, "other.txt"), "not a ctrlz log");
        var zipPath = Path.Combine(_folder, "diag.zip");

        DiagnosticsBundle.Write(zipPath, Input(), logs, Privacy);

        using var zip = ZipFile.OpenRead(zipPath);
        var names = zip.Entries.Select(entry => entry.FullName).ToList();
        Assert.Contains(DiagnosticsBundle.ReadMeEntry, names);
        Assert.Contains(DiagnosticsBundle.SummaryEntry, names);
        var logEntries = names.Where(name => name.StartsWith("logs/", StringComparison.Ordinal)).ToList();
        Assert.Equal(DiagnosticsBundle.MaxLogFiles, logEntries.Count);
        Assert.Contains("logs/ctrlz-2026-11-09.log", logEntries);
        Assert.DoesNotContain("logs/ctrlz-2026-11-01.log", logEntries);
        Assert.DoesNotContain("logs/other.txt", names);

        foreach (var entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            var text = reader.ReadToEnd();
            Assert.False(text.Contains("tonyx", StringComparison.OrdinalIgnoreCase), entry.FullName);
            Assert.False(text.Contains("secret.txt", StringComparison.OrdinalIgnoreCase), entry.FullName);
        }

        Assert.False(File.Exists(zipPath + ".partial"));
    }

    [Fact]
    public void Long_logs_keep_only_the_last_lines()
    {
        var logs = Path.Combine(_folder, "logs");
        Directory.CreateDirectory(logs);
        var lines = Enumerable.Range(1, DiagnosticsBundle.MaxLinesPerLog + 50).Select(i => "line " + i);
        File.WriteAllLines(Path.Combine(logs, "ctrlz-2026-11-22.log"), lines);
        var zipPath = Path.Combine(_folder, "diag.zip");

        DiagnosticsBundle.Write(zipPath, Input(), logs, Privacy);

        using var zip = ZipFile.OpenRead(zipPath);
        using var reader = new StreamReader(zip.GetEntry("logs/ctrlz-2026-11-22.log")!.Open());
        var kept = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(DiagnosticsBundle.MaxLinesPerLog, kept.Length);
        Assert.Equal("line 51", kept[0].TrimEnd('\r'));
    }

    [Fact]
    public void Missing_logs_folder_still_writes_a_bundle()
    {
        var zipPath = Path.Combine(_folder, "diag.zip");

        DiagnosticsBundle.Write(zipPath, Input() with { LastInstall = null }, Path.Combine(_folder, "nope"), Privacy);

        using var zip = ZipFile.OpenRead(zipPath);
        Assert.Equal(2, zip.Entries.Count);
        using var reader = new StreamReader(zip.GetEntry(DiagnosticsBundle.SummaryEntry)!.Open());
        Assert.Contains("None saved", reader.ReadToEnd());
    }
}
