// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Install;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Services;

namespace CtrlZ.App.Services;

/// <summary>Composition root. One instance is created at startup and shared by all pages.</summary>
public sealed class AppServices : IDisposable
{
    private AppServices(
        AppPaths paths,
        FileAppLog log,
        SettingsStore settingsStore,
        ProfileStore profiles,
        AppState state)
    {
        Paths = paths;
        Log = log;
        SettingsStore = settingsStore;
        Profiles = profiles;
        State = state;
    }

    public AppPaths Paths { get; }
    public FileAppLog Log { get; }
    public SettingsStore SettingsStore { get; }
    public ProfileStore Profiles { get; }
    public AppState State { get; }
    public ProcessRunner Runner { get; private init; } = null!;
    public WingetCli Winget { get; private init; } = null!;
    public DownloadService Downloads { get; private init; } = null!;
    public GitHubReleaseService GitHub { get; private init; } = null!;
    public FileInstallService FileInstaller { get; private init; } = null!;
    public AppScanner Scanner { get; private init; } = null!;
    public InstallStateStore InstallStates { get; private init; } = null!;
    public InstallEngine InstallEngine { get; private init; } = null!;
    public WingetBootstrapService WingetBootstrap { get; private init; } = null!;
    public BrowserDetector BrowserDetector { get; private init; } = null!;
    public BrowserProcessService BrowserProcesses { get; private init; } = null!;
    public BrowserBackupService BrowserBackups { get; private init; } = null!;
    public BrowserArchiveReader BrowserArchiveReader { get; private init; } = null!;
    public BrowserRestoreService BrowserRestore { get; private init; } = null!;
    public WinUtilCatalogService Catalog { get; private init; } = null!;

    public static AppServices Create()
    {
        var paths = AppPaths.CreateDefault();
        paths.EnsureCreated();
        var log = new FileAppLog(paths.Logs);
        log.Write(LogLevel.Info, $"CTRL+Z starting. Data folder: {paths.Root}");

        var settingsStore = new SettingsStore(paths.SettingsFile, log);
        var settings = settingsStore.Load();
        var profiles = new ProfileStore(paths, log);
        var state = new AppState(profiles, settingsStore, settings, log);
        state.Initialize();

        var runner = new ProcessRunner();
        var winget = new WingetCli(runner, log);
        var downloads = new DownloadService(paths, log);
        var github = new GitHubReleaseService(downloads, log);
        var fileInstaller = new FileInstallService(paths, runner, log);
        var installStates = new InstallStateStore(paths.InstallStateFile, log);
        var wingetBootstrap = new WingetBootstrapService(winget, downloads, runner, paths, log);
        var installers = new ISourceInstaller[]
        {
            new WingetSourceInstaller(winget),
            new DirectUrlSourceInstaller(downloads, fileInstaller),
            new GitHubSourceInstaller(github, downloads, fileInstaller),
            new LocalFileSourceInstaller(fileInstaller)
        };
        var installEngine = new InstallEngine(paths, installers, installStates, profiles, wingetBootstrap, log);
        var scanner = new AppScanner(winget, new RegistryAppScanner(log), new StoreAppScanner(runner, log), log);
        var browserProcesses = new BrowserProcessService(log);
        var archiveReader = new BrowserArchiveReader();
        var catalog = new WinUtilCatalogService(paths, downloads, log);

        return new AppServices(paths, log, settingsStore, profiles, state)
        {
            Runner = runner,
            Winget = winget,
            Downloads = downloads,
            GitHub = github,
            FileInstaller = fileInstaller,
            Scanner = scanner,
            InstallStates = installStates,
            InstallEngine = installEngine,
            WingetBootstrap = wingetBootstrap,
            BrowserDetector = new BrowserDetector(log),
            BrowserProcesses = browserProcesses,
            BrowserBackups = new BrowserBackupService(paths, log, browserProcesses),
            BrowserArchiveReader = archiveReader,
            BrowserRestore = new BrowserRestoreService(paths, log, browserProcesses, archiveReader),
            Catalog = catalog
        };
    }

    public void Dispose()
    {
        Log.Write(LogLevel.Info, "CTRL+Z shutting down.");
        Log.Dispose();
    }
}
