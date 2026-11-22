// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Cli;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Install;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Safety;
using CtrlZ.Core.Services;

// Same data folder as the app (including portable mode next to the exe), same stores, same engine.
var paths = AppPaths.CreateDefault();
paths.EnsureCreated();
using var log = new FileAppLog(paths.Logs);
log.Write(LogLevel.Info, "CTRL+Z command line starting.");

var settings = new SettingsStore(paths.SettingsFile, log).Load();
var profiles = new ProfileStore(paths, log);
var runner = new ProcessRunner();
var winget = new WingetCli(runner, log);
var downloads = new DownloadService(paths, log);
var github = new GitHubReleaseService(downloads, log);
var fileInstaller = new FileInstallService(paths, runner, log);
var engine = new InstallEngine(
    paths,
    new ISourceInstaller[]
    {
        new WingetSourceInstaller(winget),
        new DirectUrlSourceInstaller(downloads, fileInstaller),
        new GitHubSourceInstaller(github, downloads, fileInstaller),
        new LocalFileSourceInstaller(fileInstaller)
    },
    new InstallStateStore(paths.InstallStateFile, log),
    profiles,
    new WingetBootstrapService(winget, downloads, runner, paths, log),
    log,
    DependencyCatalog.LoadEmbedded());

var environment = new CliEnvironment
{
    Out = Console.Out,
    Error = Console.Error,
    Settings = settings,
    Profiles = profiles,
    Origins = new ProfileOriginStore(Path.Combine(paths.State, "profile-origins.json"), log),
    Approvals = new PostInstallApprovalStore(Path.Combine(paths.State, PostInstallStep.ApprovalsFileName)),
    FirstRun = new PostInstallFirstRunStore(Path.Combine(paths.State, PostInstallFirstRunStore.FileName)),
    Engine = engine,
    IsElevated = () => ElevationService.IsElevated,
    AcquireLock = () => InstallLock.TryAcquire(),
    CreateDryRun = () => new DryRunRunner(
        (id, token) => winget.SearchAsync(id, token),
        (repo, token) => github.GetLatestAsync(repo, token),
        (url, token) => downloads.HeadAsync(url, TimeSpan.FromSeconds(10), token))
};

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancel.Cancel();
};

var exitCode = await new CliRunner(environment).RunAsync(args, cancel.Token);
log.Write(LogLevel.Info, $"CTRL+Z command line finished with exit code {exitCode}.");
return exitCode;
