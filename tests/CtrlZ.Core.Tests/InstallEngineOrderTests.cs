// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Install;
using CtrlZ.Core.Models;
using CtrlZ.Core.Services;

namespace CtrlZ.Core.Tests;

/// <summary>The install engine follows the dependency plan. Local-file apps only, so no winget runs.</summary>
public class InstallEngineOrderTests
{
    private static InstallEngine Engine(TestDirectory temp, ISourceInstaller installer, DependencyCatalog? catalog = null)
    {
        var paths = new AppPaths(temp.PathOf("data"));
        paths.EnsureCreated();
        var download = new DownloadService(paths, TestFactory.Log);
        var winget = new WingetCli(new ProcessRunner(), TestFactory.Log);
        return new InstallEngine(
            paths,
            [installer],
            new InstallStateStore(paths.InstallStateFile, TestFactory.Log),
            new ProfileStore(paths, TestFactory.Log),
            new WingetBootstrapService(winget, download, new ProcessRunner(), paths, TestFactory.Log),
            TestFactory.Log,
            catalog);
    }

    private static AppEntry Local(string id, params string[] after) => new()
    {
        Id = id,
        Name = id,
        Source = AppSource.LocalFile,
        LocalPath = "missing.exe",
        AfterAppIds = after.ToList()
    };

    [Fact]
    public async Task Dependencies_install_before_the_apps_that_need_them()
    {
        using var temp = new TestDirectory();
        var installer = new FakeInstaller(AppSource.LocalFile);
        var engine = Engine(temp, installer);
        var profile = new AppProfile { Apps = [Local("app", "runtime"), Local("runtime")] };

        await engine.RunAsync(engine.CreateState(profile, null), new FakeInstallLogSink());

        Assert.Equal(new[] { "runtime", "app" }, installer.Installed);
    }

    [Fact]
    public async Task A_failed_dependency_means_its_dependents_are_not_attempted_transitively()
    {
        using var temp = new TestDirectory();
        var installer = new FakeInstaller(AppSource.LocalFile, app => app.Name == "runtime" ? 99 : 0);
        var engine = Engine(temp, installer);
        var profile = new AppProfile { Apps = [Local("runtime"), Local("plugin", "runtime"), Local("app", "plugin"), Local("other")] };

        var result = await engine.RunAsync(engine.CreateState(profile, null), new FakeInstallLogSink());

        Assert.Equal(new[] { "other" }, installer.Installed);
        var plugin = result.Items.Single(item => item.AppId == "plugin");
        var app = result.Items.Single(item => item.AppId == "app");
        Assert.Equal(InstallStatus.Failed, plugin.Status);
        Assert.Contains("needs runtime", plugin.Error);
        Assert.Equal(InstallStatus.Failed, app.Status);
        Assert.Contains("needs plugin", app.Error);
    }

    [Fact]
    public async Task Retry_after_the_dependency_succeeds_installs_the_dependents_in_order()
    {
        using var temp = new TestDirectory();
        var installer = new FakeInstaller(AppSource.LocalFile, app => app.Name == "runtime" ? 1 : 0);
        var engine = Engine(temp, installer);
        var profile = new AppProfile { Apps = [Local("app", "runtime"), Local("runtime")] };

        var first = await engine.RunAsync(engine.CreateState(profile, null), new FakeInstallLogSink());
        var second = await engine.RunAsync(first, new FakeInstallLogSink(), retryFailed: true);

        Assert.Equal(new[] { "runtime", "app" }, installer.Installed);
        Assert.All(second.Items, item => Assert.Equal(InstallStatus.Success, item.Status));
    }

    [Fact]
    public async Task An_install_order_loop_is_reported_and_does_not_stop_other_apps()
    {
        using var temp = new TestDirectory();
        var installer = new FakeInstaller(AppSource.LocalFile);
        var engine = Engine(temp, installer);
        var profile = new AppProfile { Apps = [Local("a", "b"), Local("b", "a"), Local("c")] };

        var result = await engine.RunAsync(engine.CreateState(profile, null), new FakeInstallLogSink());

        Assert.Equal(new[] { "c" }, installer.Installed);
        Assert.All(result.Items.Where(item => item.AppId is "a" or "b"), item =>
        {
            Assert.Equal(InstallStatus.Failed, item.Status);
            Assert.Contains("loop", item.Error);
        });
    }

    [Fact]
    public void Auto_added_dependencies_are_queued_first_marked_and_carried_in_the_snapshot()
    {
        using var temp = new TestDirectory();
        var engine = Engine(temp, new FakeInstaller(AppSource.Winget), DependencyCatalog.LoadEmbedded());
        var profile = new AppProfile { Apps = [new AppEntry { Id = "steam", Name = "Steam", PackageId = "Valve.Steam" }] };

        var state = engine.CreateState(profile, null);

        Assert.True(state.Items.Count > 1);
        Assert.Equal("steam", state.Items[^1].AppId);
        var added = state.ProfileSnapshot!.Apps.Where(app => app.IsAutoDependency).ToList();
        Assert.Equal(state.Items.Count - 1, added.Count);
        Assert.All(state.Items.Take(state.Items.Count - 1), item => Assert.Contains(added, app => app.Id == item.AppId));
        Assert.Single(profile.Apps);
    }

    [Fact]
    public void A_dependency_the_user_disabled_is_not_added_back()
    {
        using var temp = new TestDirectory();
        var engine = Engine(temp, new FakeInstaller(AppSource.Winget), DependencyCatalog.LoadEmbedded());
        var profile = new AppProfile
        {
            Apps =
            [
                new AppEntry { Id = "dep:vcredist.x64", Name = "VC x64", PackageId = "Microsoft.VCRedist.2015+.x64", IsAutoDependency = true, Enabled = false },
                new AppEntry { Id = "dep:vcredist.x86", Name = "VC x86", PackageId = "Microsoft.VCRedist.2015+.x86", IsAutoDependency = true, Enabled = false },
                new AppEntry { Id = "steam", Name = "Steam", PackageId = "Valve.Steam" }
            ]
        };

        var state = engine.CreateState(profile, null);

        Assert.Equal(new[] { "steam" }, state.Items.Select(item => item.AppId));
    }

    [Fact]
    public async Task A_skipped_dependency_counts_as_complete_and_does_not_block()
    {
        using var temp = new TestDirectory();
        var installer = new FakeInstaller(AppSource.LocalFile);
        var engine = Engine(temp, installer);
        var state = engine.CreateState(new AppProfile { Apps = [Local("app", "runtime"), Local("runtime")] }, null);
        state.Items.Single(item => item.AppId == "runtime").Status = InstallStatus.Skipped;

        var result = await engine.RunAsync(state, new FakeInstallLogSink());

        Assert.Equal(new[] { "app" }, installer.Installed);
        Assert.Equal(InstallStatus.Success, result.Items.Single(item => item.AppId == "app").Status);
    }

    [Fact]
    public async Task A_resumed_session_in_an_old_order_still_installs_dependencies_first()
    {
        using var temp = new TestDirectory();
        var installer = new FakeInstaller(AppSource.LocalFile);
        var engine = Engine(temp, installer);
        var state = engine.CreateState(new AppProfile { Apps = [Local("app", "runtime"), Local("runtime")] }, null);
        state.Items.Reverse();   // as saved by an older version: dependent first
        Assert.Equal("app", state.Items[0].AppId);

        await engine.RunAsync(state, new FakeInstallLogSink());

        Assert.Equal(new[] { "runtime", "app" }, installer.Installed);
    }

    [Fact]
    public void Preview_items_match_the_engine_queue_including_apps_that_will_not_install()
    {
        using var temp = new TestDirectory();
        var engine = Engine(temp, new FakeInstaller(AppSource.LocalFile));
        var profile = new AppProfile { Apps = [Local("a", "b"), Local("b", "a"), Local("c")] };

        var queue = engine.CreateState(profile, null).Items.Select(item => item.AppId);
        var plan = new CtrlZ.Core.Safety.InstallPlanner(_ => true, _ => true, engine.Catalog).Build(profile, "P");

        Assert.Equal(queue, plan.Items.Select(item => item.AppId));
        Assert.All(plan.Items.Where(item => item.AppId is "a" or "b"), item => Assert.NotNull(item.NotInstalledReason));
        Assert.Equal(1, plan.Count);
        Assert.False(plan.IsEmpty);

        var onlyLoop = new CtrlZ.Core.Safety.InstallPlanner(_ => true, _ => true, engine.Catalog)
            .Build(new AppProfile { Apps = [Local("a", "b"), Local("b", "a")] }, "P");
        Assert.True(onlyLoop.IsEmpty);
        Assert.Contains(onlyLoop.PlanWarnings, warning => warning.Contains("nothing enabled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Preview_plan_and_engine_queue_have_the_same_order()
    {
        using var temp = new TestDirectory();
        var catalog = DependencyCatalog.LoadEmbedded();
        var engine = Engine(temp, new FakeInstaller(AppSource.LocalFile), catalog);
        var profile = new AppProfile
        {
            Apps = [Local("app", "runtime"), new AppEntry { Id = "steam", Name = "Steam", PackageId = "Valve.Steam" }, Local("runtime")]
        };

        var queue = engine.CreateState(profile, null).Items.Select(item => item.AppId);
        var preview = new CtrlZ.Core.Safety.InstallPlanner(_ => true, _ => true, catalog).Build(profile, "P").Items.Select(item => item.AppId);

        Assert.Equal(queue, preview);
    }
}
