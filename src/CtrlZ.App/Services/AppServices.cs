// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Install;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Services;
using CtrlZ.Core.Updates;

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

    /// <summary>Long-running work the updater must not interrupt.</summary>
    public IAppActivityMonitor Activity { get; private init; } = null!;
    public ProcessRunner Runner { get; private init; } = null!;
    public WingetCli Winget { get; private init; } = null!;
    public DownloadService Downloads { get; private init; } = null!;
    public GitHubReleaseService GitHub { get; private init; } = null!;
    public FileInstallService FileInstaller { get; private init; } = null!;

    /// <summary>Which GitHub release each app was installed from.</summary>
    public CtrlZ.Core.AppUpdates.GitHubReleaseTracker GitHubReleases { get; private init; } = null!;

    /// <summary>Installs (and updates) apps from GitHub releases, recording the release.</summary>
    public GitHubSourceInstaller GitHubInstaller { get; private init; } = null!;
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
    public CtrlZ.Core.Tweaks.TweakService Tweaks { get; private init; } = null!;

    public static AppServices Create()
    {
        var paths = AppPaths.CreateDefault();
        paths.EnsureCreated();
        var log = new FileAppLog(paths.Logs);
        log.Write(LogLevel.Info, $"CTRL+Z starting. Data folder: {paths.Root}");

        var settingsStore = new SettingsStore(paths.SettingsFile, log);
        var settings = settingsStore.Load();

        // Before anything builds text: the profile store may name a first profile.
        CtrlZ.App.Localization.AppLanguage.Apply(settings.Language);
        var profiles = new ProfileStore(paths, log);
        var state = new AppState(profiles, settingsStore, settings, log);
        state.Initialize();

        var runner = new ProcessRunner();
        var winget = new WingetCli(runner, log);
        var downloads = new DownloadService(paths, log);
        var github = new GitHubReleaseService(downloads, log);
        var githubReleases = new CtrlZ.Core.AppUpdates.GitHubReleaseTracker(
            System.IO.Path.Combine(paths.State, CtrlZ.Core.AppUpdates.GitHubReleaseTracker.FileName));
        var fileInstaller = new FileInstallService(
            paths, runner, log, new CtrlZ.Core.AppUpdates.StartMenuShortcutWriter(log), () => settings.CreatePortableShortcuts);
        var installStates = new InstallStateStore(paths.InstallStateFile, log);
        var wingetBootstrap = new WingetBootstrapService(winget, downloads, runner, paths, log);
        var githubInstaller = new GitHubSourceInstaller(github, downloads, fileInstaller, githubReleases);
        var installers = new ISourceInstaller[]
        {
            new WingetSourceInstaller(winget),
            new DirectUrlSourceInstaller(downloads, fileInstaller),
            githubInstaller,
            new LocalFileSourceInstaller(fileInstaller)
        };
        var installEngine = new InstallEngine(
            paths, installers, installStates, profiles, wingetBootstrap, log, DependencyCatalog.LoadEmbedded());
        var scanner = new AppScanner(winget, new RegistryAppScanner(log), new StoreAppScanner(runner, log), log);
        var browserProcesses = new BrowserProcessService(log);
        var archiveReader = new BrowserArchiveReader();
        var catalog = new WinUtilCatalogService(paths, downloads, log);

        return new AppServices(paths, log, settingsStore, profiles, state)
        {
            Activity = new AppActivityMonitor(),
            Runner = runner,
            Winget = winget,
            Downloads = downloads,
            GitHub = github,
            GitHubReleases = githubReleases,
            GitHubInstaller = githubInstaller,
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
            Catalog = catalog,
            Tweaks = new CtrlZ.Core.Tweaks.TweakService(
                new CtrlZ.Core.Tweaks.WindowsUserRegistry(), System.IO.Path.Combine(paths.State, "tweak-undo.json"), log)
        };
    }

    public void Dispose()
    {
        Log.Write(LogLevel.Info, "CTRL+Z shutting down.");
        Log.Dispose();
    }
}
