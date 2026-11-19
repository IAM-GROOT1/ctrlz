// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Reflection;
using System.Text.Json;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Models;
using CtrlZ.Core.Safety;
using CtrlZ.Core.Services;

namespace CtrlZ.Core.Tests;

/// <summary>
/// Privacy guarantees for the safety checks: no personal names in logs, no paths in the saved
/// state, no file contents read, and no personal paths in an exported dry-run report.
/// </summary>
public class SafetyPrivacyTests
{
    // ---- (a) logs carry counts, never names ---------------------------------------

    [Fact]
    public void A_full_checklist_scan_writes_only_counts_to_the_log()
    {
        using var workspace = new TestDirectory();
        var logs = workspace.CreateDirectory("logs");

        // Deliberately distinctive names: if any of them reach the log, the test fails.
        const string secretFolderName = "PrivateTaxReturns";
        const string secretFileName = "passport-scan-2019.pdf";
        var documents = workspace.CreateDirectory(@"home\Documents\PrivateTaxReturns");
        workspace.Write(@"home\Documents\PrivateTaxReturns\passport-scan-2019.pdf", "sensitive");
        workspace.Write(@"home\Desktop\diary.txt", "sensitive");

        using var log = new FileAppLog(logs);
        var scanner = new ReadinessScanner(log: log);

        var request = new ReadinessRequest
        {
            Environment = EnvironmentFor(workspace),
            UserFolders =
            [
                new UserFolder(secretFolderName, documents),
                new UserFolder("Desktop", workspace.PathOf(@"home\Desktop"))
            ],
            Now = DateTimeOffset.UtcNow,
            RecentDays = 0,
            LargeFolderBytes = 0,
            FolderOptions = new FolderUsageOptions { MaxDepth = 8, MaxFiles = 1000, MaxDuration = TimeSpan.Zero }
        };

        scanner.Scan(request);
        log.Dispose();

        var written = ReadAllLogs(logs);

        Assert.Contains("Readiness check:", written);
        Assert.DoesNotContain(secretFolderName, written, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secretFileName, written, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("diary.txt", written, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"C:\", written, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_diagnostics_summary_is_numbers_only()
    {
        var report = new ReadinessReport
        {
            Checks =
            [
                new ReadinessCheck { Id = "folder.documents", Title = "Documents", Status = ReadinessStatus.Warning },
                new ReadinessCheck { Id = "games", Title = "Games", Status = ReadinessStatus.Pass },
                new ReadinessCheck { Id = "dev", Title = "Developer", Status = ReadinessStatus.Pass }
            ]
        };

        var summary = SafetyDiagnostics.Summary(report);

        Assert.Contains("1 warning", summary);
        Assert.DoesNotContain("Documents", summary);
        Assert.DoesNotContain(@"\", summary);
        Assert.DoesNotContain("/", summary);
    }

    [Fact]
    public void Drift_diagnostics_are_numbers_only()
    {
        var report = new DriftReport
        {
            Entries =
            [
                new DriftEntry { Kind = DriftKind.Missing, Key = "missing:x", Name = @"C:\Users\bob\App.exe" }
            ]
        };

        var summary = SafetyDiagnostics.DriftSummary(report);

        Assert.Contains("1 missing", summary);
        Assert.DoesNotContain(@"C:\", summary);
        Assert.DoesNotContain("bob", summary);
    }

    [Theory]
    [InlineData(@"Access to the path 'C:\Users\bob\AppData\Local\Chrome' is denied.", @"C:\Users\bob\AppData\Local\Chrome")]
    [InlineData(@"Could not open C:\Users\bob\Documents\a.txt.", @"C:\Users\bob\Documents\a.txt")]
    [InlineData(@"Failed on \\server\share\folder\file.bin", @"\\server\share\folder\file.bin")]
    public void Path_redaction_removes_drive_and_unc_paths(string message, string pathInMessage)
    {
        var redacted = LogPrivacy.RedactPaths(message);

        Assert.DoesNotContain(pathInMessage, redacted);
        Assert.Contains("<path>", redacted);
    }

    [Fact]
    public void Path_redaction_leaves_ordinary_text_alone()
    {
        Assert.Equal("Could not inspect Firefox (UnauthorizedAccessException).",
            LogPrivacy.RedactPaths("Could not inspect Firefox (UnauthorizedAccessException)."));
        Assert.Equal(string.Empty, LogPrivacy.RedactPaths(null));
    }

    // ---- (b) saved state has ids and flags, not paths ------------------------------

    [Fact]
    public void Safety_state_never_stores_a_path()
    {
        using var workspace = new TestDirectory();
        var statePath = workspace.PathOf("state", "safety-state.json");

        var store = new SafetyStateStore(statePath, NullAppLog.Instance);
        var state = store.Load();
        state.HandledChecks.Add("folder.desktop");
        state.IgnoredChecks.Add(ReadinessReminders.IdPrefix + "export-password-manager");
        state.IgnoredDriftKeys.Add(DriftAnalyzer.BuildKey(DriftKind.Extra, "Vendor.App", "App"));
        store.Save(state);

        var json = File.ReadAllText(statePath);

        Assert.DoesNotContain(@"\", json);
        Assert.DoesNotContain("/", json);
        Assert.DoesNotContain(@"C:", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(workspace.Root, json, StringComparison.OrdinalIgnoreCase);

        // And the ids that are stored are the checklist's own synthetic ids.
        var reloaded = JsonSerializer.Deserialize<SafetyState>(json, JsonDefaults.Profile);
        Assert.NotNull(reloaded);
        Assert.Contains("folder.desktop", reloaded!.HandledChecks);
    }

    // ---- (c) .env and .ssh are never opened ---------------------------------------

    [Fact]
    public void Env_files_and_ssh_keys_are_listed_without_being_opened()
    {
        using var workspace = new TestDirectory();
        var home = workspace.CreateDirectory("home");
        var envPath = workspace.Write(@"home\Projects\app\.env", "API_KEY=super-secret-value");
        var keyPath = workspace.Write(@"home\.ssh\id_ed25519", "-----BEGIN PRIVATE KEY-----");
        workspace.Write(@"home\.ssh\known_hosts", "example");

        // Hold both files open exclusively: any attempt to read them would fail, so a correct
        // count proves the probe only looked at the directory listing.
        using var envLock = new FileStream(envPath, FileMode.Open, FileAccess.Read, FileShare.None);
        using var keyLock = new FileStream(keyPath, FileMode.Open, FileAccess.Read, FileShare.None);

        var items = new DeveloperItemsProbe(() => 0).Probe(EnvironmentFor(workspace));

        Assert.Equal(1, items.Single(item => item.Id == "env-files").Count);
        Assert.Equal(2, items.Single(item => item.Id == "ssh").Count);

        var text = string.Join(" | ", items.Select(item => $"{item.Label} {item.Note}"));
        Assert.DoesNotContain("super-secret-value", text);
        Assert.DoesNotContain("id_ed25519", text);
        Assert.DoesNotContain("known_hosts", text);
    }

    // ---- (d) the exported report adds no new personal paths -----------------------

    [Fact]
    public async Task The_exported_dry_run_report_adds_no_personal_paths()
    {
        using var workspace = new TestDirectory();
        var privateFolder = workspace.CreateDirectory(@"home\PrivateStuff");
        var installer = workspace.Write(@"home\PrivateStuff\setup-1.0.exe", "x");

        var app = new AppEntry
        {
            Name = "Private Tool",
            Source = AppSource.LocalFile,
            LocalPath = installer
        };

        var plan = new InstallPlanner(fileExists: _ => true, isSigned: _ => true)
            .Build(new AppProfile { Apps = [app] }, "Test");

        var runner = new DryRunRunner(
            (_, _) => Task.FromResult<IReadOnlyList<WingetPackage>>([]),
            (_, _) => Task.FromException<GitHubRelease>(new InvalidOperationException()),
            (_, _) => Task.FromException<long?>(new DownloadException("n/a")));

        var report = await runner.RunAsync(plan, new Dictionary<string, AppEntry> { [app.Id] = app });
        var text = report.ToText();

        Assert.Contains("Private Tool", text);
        Assert.DoesNotContain("PrivateStuff", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(workspace.Root, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"C:\", text, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the checklist has no way to reach the network ----------------------------

    [Fact]
    public void No_checklist_type_can_make_a_network_request()
    {
        var networkTypes = new[] { typeof(DownloadService), typeof(GitHubReleaseService), typeof(HttpClient) };

        // DryRunRunner is the only type allowed to hold network-capable dependencies, and it is
        // only ever invoked from the explicit "Dry run" command.
        var offenders = typeof(ReadinessScanner).Assembly
            .GetTypes()
            .Where(type => type.Namespace == "CtrlZ.Core.Safety")
            .Where(type => type != typeof(DryRunRunner))
            .SelectMany(type =>
                type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .SelectMany(ctor => ctor.GetParameters())
                    .Select(parameter => (Type: type, Parameter: parameter.ParameterType))
                    .Concat(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                        .Select(field => (Type: type, Parameter: field.FieldType))))
            .Where(entry => networkTypes.Any(network => network.IsAssignableFrom(entry.Parameter)))
            .Select(entry => entry.Type.Name + "." + entry.Parameter.Name)
            .ToList();

        Assert.Empty(offenders);
    }

    // ---- helpers ------------------------------------------------------------------

    private static SafetyEnvironment EnvironmentFor(TestDirectory workspace) => new()
    {
        HomeDirectory = workspace.PathOf("home"),
        AppData = workspace.PathOf("home"),
        LocalAppData = workspace.PathOf("home"),
        ProgramData = workspace.PathOf("home"),
        ProgramFiles = workspace.PathOf("home"),
        ProgramFilesX86 = workspace.PathOf("home"),
        Documents = workspace.PathOf(@"home\Documents"),
        SavedGames = workspace.PathOf(@"home\SavedGames"),
        FixedDrives = []
    };

    private static string ReadAllLogs(string logsFolder)
    {
        if (!Directory.Exists(logsFolder))
        {
            return string.Empty;
        }

        return string.Join(
            Environment.NewLine,
            Directory.GetFiles(logsFolder)
                .OrderBy(path => path)
                .Select(File.ReadAllText));
    }
}
