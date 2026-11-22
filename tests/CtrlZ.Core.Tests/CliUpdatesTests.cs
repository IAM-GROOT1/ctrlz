// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.AppUpdates;
using CtrlZ.Core.Cli;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Install;
using CtrlZ.Core.Models;
using CtrlZ.Core.Safety;
using CtrlZ.Core.Services;

namespace CtrlZ.Core.Tests;

public sealed class CliUpdatesTests : IDisposable
{
    private readonly TestDirectory _temp = new();
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private readonly List<string> _upgraded = new();

    public void Dispose() => _temp.Dispose();

    private static AppUpdate Update(string id, bool pinned = false, string available = "2.0") =>
        new(id, id, "1.0", available, "winget", pinned);

    private AppUpdateIgnoreStore Ignores() => new(_temp.PathOf("state", AppUpdateIgnoreStore.FileName));

    private CliRunner Cli(IReadOnlyList<AppUpdate> updates, int exitCode = 0, bool busy = false)
    {
        var paths = new AppPaths(_temp.PathOf("data"));
        paths.EnsureCreated();
        var profiles = new ProfileStore(paths, TestFactory.Log);
        var download = new DownloadService(paths, TestFactory.Log);
        var winget = new WingetCli(new ProcessRunner(), TestFactory.Log);
        var engine = new InstallEngine(
            paths, [new FakeInstaller(AppSource.LocalFile)], new InstallStateStore(paths.InstallStateFile, TestFactory.Log),
            profiles, new WingetBootstrapService(winget, download, new ProcessRunner(), paths, TestFactory.Log), TestFactory.Log);

        return new CliRunner(new CliEnvironment
        {
            Out = _out,
            Error = _err,
            Settings = new AppSettings(),
            Profiles = profiles,
            Origins = new ProfileOriginStore(Path.Combine(paths.State, "o.json")),
            Approvals = new PostInstallApprovalStore(Path.Combine(paths.State, "a.json")),
            FirstRun = new PostInstallFirstRunStore(Path.Combine(paths.State, "f.json")),
            Engine = engine,
            IsElevated = () => true,
            AcquireLock = () => busy ? null : InstallLock.TryAcquire(@"Local\CtrlZ.Test." + Guid.NewGuid().ToString("n")),
            CreateDryRun = () => throw new InvalidOperationException(),
            ListUpdates = _ => Task.FromResult(updates),
            Upgrade = (id, _, _) =>
            {
                _upgraded.Add(id);
                return Task.FromResult((exitCode, string.Empty));
            },
            UpdateIgnores = Ignores()
        });
    }

    [Theory]
    [InlineData(new[] { "updates" }, true)]
    [InlineData(new[] { "updates", "--all", "--yes" }, true)]
    [InlineData(new[] { "updates", "--id", "A.App", "--id", "B.App", "--yes" }, true)]
    [InlineData(new[] { "updates", "--all", "--id", "A.App" }, false)]
    [InlineData(new[] { "updates", "--yes" }, false)]
    [InlineData(new[] { "updates", "--id" }, false)]
    [InlineData(new[] { "updates", "profile.json" }, false)]
    [InlineData(new[] { "updates", "--allow-commands" }, false)]
    [InlineData(new[] { "install", "p.json", "--all" }, false)]
    public void Updates_arguments_are_parsed_strictly(string[] args, bool valid)
    {
        Assert.Equal(valid, CliParser.Parse(args).IsValid);
    }

    [Fact]
    public async Task Listing_changes_nothing()
    {
        var exit = await Cli([Update("A.App"), Update("P.App", pinned: true)]).RunAsync(["updates"]);

        Assert.Equal(CliExitCode.Success, exit);
        Assert.Empty(_upgraded);
        Assert.Contains("A.App", _out.ToString());
        Assert.Contains("[pinned]", _out.ToString());
    }

    [Fact]
    public async Task Without_yes_nothing_is_updated()
    {
        var exit = await Cli([Update("A.App")]).RunAsync(["updates", "--all"]);

        Assert.Equal(CliExitCode.Cancelled, exit);
        Assert.Empty(_upgraded);
    }

    [Fact]
    public async Task Update_all_skips_pinned_and_ignored_apps()
    {
        var ignored = Update("I.App");
        Ignores().Ignore(ignored);

        var exit = await Cli([Update("A.App"), Update("P.App", pinned: true), ignored]).RunAsync(["updates", "--all", "--yes"]);

        Assert.Equal(CliExitCode.Success, exit);
        Assert.Equal(new[] { "A.App" }, _upgraded);
    }

    [Fact]
    public async Task A_pinned_app_is_updated_when_named_explicitly()
    {
        var exit = await Cli([Update("A.App"), Update("P.App", pinned: true)]).RunAsync(["updates", "--id", "p.app", "--yes"]);

        Assert.Equal(CliExitCode.Success, exit);
        Assert.Equal(new[] { "P.App" }, _upgraded);
    }

    [Fact]
    public async Task An_id_with_no_update_is_a_usage_error_and_changes_nothing()
    {
        var exit = await Cli([Update("A.App")]).RunAsync(["updates", "--id", "Missing.App", "--yes"]);

        Assert.Equal(CliExitCode.Usage, exit);
        Assert.Empty(_upgraded);
    }

    [Fact]
    public async Task A_failed_update_exits_2()
    {
        var exit = await Cli([Update("A.App")], exitCode: 1603).RunAsync(["updates", "--all", "--yes", "--silent"]);

        Assert.Equal(CliExitCode.PartialFailure, exit);
    }

    [Fact]
    public async Task Another_running_install_exits_7_and_updates_nothing()
    {
        var exit = await Cli([Update("A.App")], busy: true).RunAsync(["updates", "--all", "--yes"]);

        Assert.Equal(CliExitCode.Busy, exit);
        Assert.Empty(_upgraded);
    }

    [Fact]
    public async Task Names_from_winget_are_printed_safely()
    {
        await Cli([new AppUpdate("Evil\u001b[2J", "Evil.App", "1", "2", "winget", false)]).RunAsync(["updates"]);

        var output = _out.ToString();
        Assert.False(output.Contains('\u001b'), "OUT=" + output.Replace("\u001b", "<ESC>") + " ERR=" + _err);
        Assert.Contains("\\u001B", output);
    }
}
