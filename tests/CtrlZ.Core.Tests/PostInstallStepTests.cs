// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Install;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Tests;

public class PostInstallStepTests
{
    private sealed class QuietLog : IAppLog
    {
        public string LogFilePath => string.Empty;
        public string LogsFolder => string.Empty;
        public event EventHandler<LogLine>? LineWritten { add { } remove { } }
        public void Write(LogLevel level, string message, Exception? exception = null) { }
        public void Dispose() { }
    }

    private sealed class Fixture
    {
        public required string Dir { get; init; }
        public required ProfileOriginStore Origins { get; init; }
        public required PostInstallApprovalStore Approvals { get; init; }
        public required PostInstallStep Step { get; init; }
        public required AppProfile Profile { get; init; }

        public AppEntry App => Profile.Apps[0];
    }

    private static Fixture Create(string exe = "cmd.exe", int timeoutSeconds = 30, params string[] args)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ctrlz-step-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        var origins = new ProfileOriginStore(Path.Combine(dir, "origins.json"));
        var approvals = new PostInstallApprovalStore(Path.Combine(dir, "approvals.json"));
        var profile = new AppProfile { Id = "p-" + Guid.NewGuid().ToString("n")[..6], Name = "P" };
        profile.Apps.Add(new AppEntry
        {
            Id = "a1",
            Name = "App",
            Enabled = true,
            PostInstall = new PostInstallCommand
            {
                Executable = exe,
                Arguments = args.ToList(),
                Enabled = true,
                TimeoutSeconds = timeoutSeconds
            }
        });

        return new Fixture
        {
            Dir = dir,
            Origins = origins,
            Approvals = approvals,
            Step = new PostInstallStep(origins, approvals, new PostInstallRunner(new QuietLog())),
            Profile = profile
        };
    }

    private static Task<PostInstallOutcome> Run(Fixture f, bool installSucceeded = true) =>
        f.Step.RunAfterInstallAsync(f.Profile, f.App, installSucceeded);

    [Fact]
    public async Task A_local_profile_runs_its_command_after_a_successful_install()
    {
        var f = Create(args: new[] { "/c", "exit", "0" });
        ProfileOriginRecorder.RecordCreated(f.Origins, f.Profile);

        var outcome = await Run(f);

        Assert.Equal(PostInstallOutcomeKind.Ran, outcome.Kind);
    }

    [Fact]
    public async Task No_command_runs_when_the_install_failed()
    {
        var f = Create(args: new[] { "/c", "exit", "0" });
        ProfileOriginRecorder.RecordCreated(f.Origins, f.Profile);

        var outcome = await Run(f, installSucceeded: false);

        Assert.Equal(PostInstallOutcomeKind.NotApplicable, outcome.Kind);
    }

    [Fact]
    public async Task An_imported_profile_is_blocked()
    {
        var f = Create(args: new[] { "/c", "exit", "0" });
        ProfileOriginRecorder.RecordImported(f.Origins, f.Profile);

        Assert.Equal(PostInstallOutcomeKind.Blocked, (await Run(f)).Kind);
    }

    [Fact]
    public async Task An_imported_profile_with_enabled_true_is_still_blocked()
    {
        var f = Create(args: new[] { "/c", "exit", "0" });
        f.App.PostInstall!.Enabled = true;
        ProfileOriginRecorder.RecordImported(f.Origins, f.Profile);

        Assert.Equal(PostInstallOutcomeKind.Blocked, (await Run(f)).Kind);
    }

    [Fact]
    public async Task A_missing_origin_is_blocked()
    {
        var f = Create(args: new[] { "/c", "exit", "0" });

        Assert.Equal(PostInstallOutcomeKind.Blocked, (await Run(f)).Kind);
    }

    [Fact]
    public async Task A_corrupt_origin_file_is_blocked()
    {
        var f = Create(args: new[] { "/c", "exit", "0" });
        ProfileOriginRecorder.RecordCreated(f.Origins, f.Profile);
        File.WriteAllText(Path.Combine(f.Dir, "origins.json"), "{ broken");
        var reopened = new ProfileOriginStore(Path.Combine(f.Dir, "origins.json"));
        var step = new PostInstallStep(reopened, f.Approvals, new PostInstallRunner(new QuietLog()));

        Assert.Equal(PostInstallOutcomeKind.Blocked, (await step.RunAfterInstallAsync(f.Profile, f.App, true)).Kind);
    }

    [Fact]
    public async Task An_externally_edited_local_file_is_blocked()
    {
        var f = Create(args: new[] { "/c", "exit", "0" });
        ProfileOriginRecorder.RecordSaved(f.Origins, f.Profile);
        f.App.PostInstall!.Arguments.Add("--extra");
        ProfileOriginRecorder.LoadForUse(f.Origins, f.Profile);

        Assert.Equal(PostInstallOutcomeKind.Blocked, (await Run(f)).Kind);
    }

    [Fact]
    public async Task An_approved_command_runs()
    {
        var f = Create(args: new[] { "/c", "exit", "0" });
        ProfileOriginRecorder.RecordImported(f.Origins, f.Profile);
        f.Approvals.Approve(f.Profile.Id, f.App.PostInstall!);

        Assert.Equal(PostInstallOutcomeKind.Ran, (await Run(f)).Kind);
    }

    [Fact]
    public async Task An_edited_command_is_blocked_until_reapproved()
    {
        var f = Create(args: new[] { "/c", "exit", "0" });
        ProfileOriginRecorder.RecordImported(f.Origins, f.Profile);
        f.Approvals.Approve(f.Profile.Id, f.App.PostInstall!);
        f.App.PostInstall!.Arguments.Add("--changed");

        Assert.Equal(PostInstallOutcomeKind.Blocked, (await Run(f)).Kind);

        f.Approvals.Approve(f.Profile.Id, f.App.PostInstall!);
        Assert.Equal(PostInstallOutcomeKind.Ran, (await Run(f)).Kind);
    }

    [Fact]
    public async Task A_command_changed_between_approval_and_execution_is_blocked()
    {
        var f = Create(args: new[] { "/c", "exit", "0" });
        ProfileOriginRecorder.RecordImported(f.Origins, f.Profile);
        f.Approvals.Approve(f.Profile.Id, f.App.PostInstall!);

        // Someone rewrites the profile after the approval and before the step runs.
        f.App.PostInstall!.Arguments.Clear();
        f.App.PostInstall!.Arguments.Add("/c");
        f.App.PostInstall!.Arguments.Add("exit");
        f.App.PostInstall!.Arguments.Add("9");

        Assert.Equal(PostInstallOutcomeKind.Blocked, (await Run(f)).Kind);
    }

    [Fact]
    public async Task A_timeout_returns_timed_out_and_does_not_throw()
    {
        var f = Create(timeoutSeconds: 5, args: new[] { "/c", "ping", "-n", "30", "127.0.0.1" });
        ProfileOriginRecorder.RecordCreated(f.Origins, f.Profile);

        var outcome = await Run(f);

        Assert.Equal(PostInstallOutcomeKind.TimedOut, outcome.Kind);
    }

    [Fact]
    public async Task A_failing_command_returns_failed_and_does_not_throw()
    {
        var f = Create(args: new[] { "/c", "exit", "3" });
        ProfileOriginRecorder.RecordCreated(f.Origins, f.Profile);

        var outcome = await Run(f);

        Assert.Equal(PostInstallOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(3, outcome.ExitCode);
    }
}
