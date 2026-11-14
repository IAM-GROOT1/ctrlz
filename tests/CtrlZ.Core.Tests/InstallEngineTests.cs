using CtrlZ.Core.Install;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Models;
using CtrlZ.Core.Services;

namespace CtrlZ.Core.Tests;

public class InstallEngineTests
{
    private static (InstallEngine Engine, InstallStateStore StateStore, AppPaths Paths) CreateEngine(TestDirectory temp, ISourceInstaller installer)
    {
        var paths = new AppPaths(temp.PathOf("data"));
        paths.EnsureCreated();
        var stateStore = new InstallStateStore(paths.InstallStateFile, TestFactory.Log);
        var profiles = new ProfileStore(paths, TestFactory.Log);
        var download = new DownloadService(paths, TestFactory.Log);
        var winget = new WingetCli(new ProcessRunner(), TestFactory.Log);
        var bootstrap = new WingetBootstrapService(winget, download, new ProcessRunner(), paths, TestFactory.Log);
        var engine = new InstallEngine(paths, [installer], stateStore, profiles, bootstrap, TestFactory.Log);
        return (engine, stateStore, paths);
    }

    [Fact]
    public async Task Successful_install_marks_item_complete()
    {
        using var temp = new TestDirectory();
        var installer = new FakeInstaller(AppSource.LocalFile);
        var (engine, stateStore, _) = CreateEngine(temp, installer);
        var profile = new AppProfile { Name = "Fresh PC" };
        profile.Apps.Add(new AppEntry { Name = "App One", Source = AppSource.LocalFile, LocalPath = "missing.exe" });

        var state = engine.CreateState(profile, null);
        var result = await engine.RunAsync(state, new FakeInstallLogSink());

        Assert.Equal(InstallStatus.Success, result.Items[0].Status);
        Assert.True(result.IsFinished);
        Assert.Equal(1, result.CompletedCount);
        Assert.NotNull(stateStore.Load());
    }

    [Fact]
    public async Task One_failure_does_not_stop_later_apps()
    {
        using var temp = new TestDirectory();
        var installer = new FakeInstaller(AppSource.LocalFile, app => app.Name == "Broken" ? 99 : 0);
        var (engine, _, _) = CreateEngine(temp, installer);
        var profile = new AppProfile { Name = "Mixed" };
        profile.Apps.Add(new AppEntry { Name = "Broken", Source = AppSource.LocalFile, LocalPath = "missing.exe" });
        profile.Apps.Add(new AppEntry { Name = "Works", Source = AppSource.LocalFile, LocalPath = "missing.exe" });

        var result = await engine.RunAsync(engine.CreateState(profile, null), new FakeInstallLogSink());

        Assert.Equal(InstallStatus.Failed, result.Items[0].Status);
        Assert.Equal(InstallStatus.Success, result.Items[1].Status);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(1, result.CompletedCount);
    }

    [Fact]
    public async Task Retry_failed_runs_only_unsuccessful_items()
    {
        using var temp = new TestDirectory();
        var installer = new FakeInstaller(AppSource.LocalFile, app => app.Name == "Flaky" ? 1 : 0);
        var (engine, _, _) = CreateEngine(temp, installer);
        var profile = new AppProfile { Name = "Retry" };
        profile.Apps.Add(new AppEntry { Name = "Flaky", Source = AppSource.LocalFile, LocalPath = "missing.exe" });
        profile.Apps.Add(new AppEntry { Name = "Stable", Source = AppSource.LocalFile, LocalPath = "missing.exe" });

        var state = engine.CreateState(profile, null);
        await engine.RunAsync(state, new FakeInstallLogSink());
        Assert.Equal(InstallStatus.Failed, state.Items[0].Status);
        Assert.Equal(InstallStatus.Success, state.Items[1].Status);

        await engine.RunAsync(state, new FakeInstallLogSink(), retryFailed: true);
        Assert.Equal(InstallStatus.Success, state.Items[0].Status);
        Assert.Equal(2, installer.Installed.Count);
    }

    [Fact]
    public void State_roundtrips_profile_snapshot()
    {
        using var temp = new TestDirectory();
        var installer = new FakeInstaller(AppSource.LocalFile);
        var (engine, stateStore, _) = CreateEngine(temp, installer);
        var profile = new AppProfile { Name = "Snapshot" };
        profile.Apps.Add(new AppEntry { Name = "One", Source = AppSource.LocalFile, LocalPath = "one.exe" });
        var state = engine.CreateState(profile, null);

        var loaded = stateStore.Load();
        Assert.NotNull(loaded);
        Assert.Equal("Snapshot", loaded!.ProfileName);
        Assert.Equal("One", loaded.ProfileSnapshot!.Apps[0].Name);
    }
}
