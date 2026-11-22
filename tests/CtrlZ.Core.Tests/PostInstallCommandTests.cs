// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Install;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Tests;

public class PostInstallCommandTests
{
    // ---- helpers -------------------------------------------------------------------

    private sealed class CapturingLog : IAppLog
    {
        public List<string> Lines { get; } = new();
        public string LogFilePath => string.Empty;
        public string LogsFolder => string.Empty;
        public event EventHandler<LogLine>? LineWritten { add { } remove { } }
        public void Write(LogLevel level, string message, Exception? exception = null) => Lines.Add(message);
        public void Dispose() { }
    }

    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "ctrlz-pic-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static PostInstallCommand Command(string exe, params string[] args) => new()
    {
        Executable = exe,
        Arguments = args.ToList(),
        Enabled = true,
        TimeoutSeconds = 30
    };

    private static AppEntry AppWith(PostInstallCommand? command, string notes = "") => new()
    {
        Id = "app-" + Guid.NewGuid().ToString("n")[..6],
        Name = "Test App",
        Enabled = true,
        Notes = string.IsNullOrEmpty(notes) ? null : notes,
        PostInstall = command
    };

    private static AppProfile ProfileWith(AppEntry app) => new() { Id = "p1", Name = "P", Apps = { app } };

    // ---- gate ----------------------------------------------------------------------

        [Fact]
    public void An_imported_profile_blocks_every_command()
    {
        var dir = NewTempDir();
        var origins = new ProfileOriginStore(Path.Combine(dir, "origins.json"));
        var gate = new PostInstallCommandGate(origins, new PostInstallApprovalStore(Path.Combine(dir, "a.json")));
        var app = AppWith(Command("cmd.exe", "/c", "echo", "hi"));
        var profile = ProfileWith(app);
        origins.Set(profile.Id, ProfileOrigin.Imported);

        var verdict = gate.Evaluate(profile).Single();

        Assert.Equal(CommandDecision.BlockedUntrusted, verdict.Decision);
        Assert.False(verdict.MayRun);
    }

    [Fact]
    public void Enabled_true_inside_an_imported_profile_does_not_help()
    {
        var dir = NewTempDir();
        var origins = new ProfileOriginStore(Path.Combine(dir, "origins.json"));
        var gate = new PostInstallCommandGate(origins, new PostInstallApprovalStore(Path.Combine(dir, "a.json")));
        var command = Command("cmd.exe", "/c", "echo", "hi");
        command.Enabled = true;
        var profile = ProfileWith(AppWith(command));
        origins.Set(profile.Id, ProfileOrigin.Imported);

        Assert.Equal(CommandDecision.BlockedUntrusted, gate.Evaluate(profile).Single().Decision);
    }

        [Fact]
    public void A_reviewed_imported_command_runs()
    {
        var dir = NewTempDir();
        var origins = new ProfileOriginStore(Path.Combine(dir, "origins.json"));
        var approvals = new PostInstallApprovalStore(Path.Combine(dir, "a.json"));
        var gate = new PostInstallCommandGate(origins, approvals);
        var command = Command("cmd.exe", "/c", "echo", "hi");
        var profile = ProfileWith(AppWith(command));
        origins.Set(profile.Id, ProfileOrigin.Imported);
        approvals.Approve(profile.Id, command);

        Assert.Equal(CommandDecision.Run, gate.Evaluate(profile).Single().Decision);
    }

    [Fact]
    public void Editing_a_reviewed_command_disables_it_again()
    {
        var dir = NewTempDir();
        var origins = new ProfileOriginStore(Path.Combine(dir, "origins.json"));
        var approvals = new PostInstallApprovalStore(Path.Combine(dir, "a.json"));
        var gate = new PostInstallCommandGate(origins, approvals);
        var command = Command("cmd.exe", "/c", "echo", "hi");
        var profile = ProfileWith(AppWith(command));
        origins.Set(profile.Id, ProfileOrigin.Imported);
        approvals.Approve(profile.Id, command);

        command.Arguments.Add("--extra");

        Assert.Equal(CommandDecision.BlockedUntrusted, gate.Evaluate(profile).Single().Decision);
    }

        // ---- runner --------------------------------------------------------------------

    [Fact]
    public async Task A_failing_command_reports_its_exit_code_and_does_not_throw()
    {
        var log = new CapturingLog();
        var runner = new PostInstallRunner(log);

        var result = await runner.RunAsync(AppWith(Command("cmd.exe", "/c", "exit", "3")), Command("cmd.exe", "/c", "exit", "3"));

        Assert.False(result.Succeeded);
        Assert.Equal(3, result.ExitCode);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task A_command_that_overruns_its_timeout_is_killed()
    {
        var log = new CapturingLog();
        var runner = new PostInstallRunner(log);
        var command = Command("cmd.exe", "/c", "ping", "-n", "30", "127.0.0.1");
        command.TimeoutSeconds = 5;

        var result = await runner.RunAsync(AppWith(command), command);

        Assert.True(result.TimedOut);
        Assert.False(result.Succeeded);
        Assert.Contains(log.Lines, line => line.Contains("timed out", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Arguments_are_never_written_to_the_log()
    {
        var log = new CapturingLog();
        var runner = new PostInstallRunner(log);
        var command = Command("cmd.exe", "/c", "echo", "super-secret-argument");

        await runner.RunAsync(AppWith(command), command);

        Assert.DoesNotContain(log.Lines, line => line.Contains("super-secret-argument", StringComparison.Ordinal));
        Assert.Contains(log.Lines, line => line.Contains("exit=", StringComparison.Ordinal));
    }

    // ---- notes ---------------------------------------------------------------------

    [Fact]
    public void Notes_are_hidden_from_reports_unless_opted_in()
    {
        Assert.Equal(NotesPrivacy.Redacted, NotesPrivacy.ForReport("licence ABC-123", includeNotes: false));
        Assert.Equal("licence ABC-123", NotesPrivacy.ForReport("licence ABC-123", includeNotes: true));
        Assert.Null(NotesPrivacy.ForReport(null, includeNotes: true));
    }

    [Fact]
    public void Notes_never_reach_a_log_line()
    {
        Assert.Equal("[note present]", NotesPrivacy.ForLog("licence ABC-123"));
        Assert.DoesNotContain("ABC-123", NotesPrivacy.ForLog("licence ABC-123"));
    }
}
