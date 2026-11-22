// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Services;

/// <summary>Thin, testable wrapper around the winget executable.</summary>
public sealed class WingetCli
{
    private readonly ProcessRunner _runner;
    private readonly IAppLog _log;
    private string? _executable;
    private string? _version;
    private readonly SemaphoreSlim _discoveryLock = new(1, 1);

    public WingetCli(ProcessRunner runner, IAppLog log)
    {
        _runner = runner;
        _log = log;
    }

    public string Executable => _executable ?? "winget";
    public string? Version => _version;

    public void ResetDiscovery()
    {
        _executable = null;
        _version = null;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        return await EnsureDiscoveredAsync(cancellationToken).ConfigureAwait(false) != null;
    }

    public async Task<string?> EnsureDiscoveredAsync(CancellationToken cancellationToken = default)
    {
        if (_executable is not null)
        {
            return _executable;
        }

        await _discoveryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_executable is not null)
            {
                return _executable;
            }

            foreach (var candidate in CandidatePaths())
            {
                var result = await _runner.RunAsync(
                    candidate,
                    ["--version"],
                    new ProcessRunOptions { Timeout = TimeSpan.FromSeconds(20) },
                    cancellationToken).ConfigureAwait(false);

                if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput))
                {
                    _executable = candidate;
                    _version = result.StandardOutput.Trim().Split('\n')[0].Trim();
                    _log.Write(LogLevel.Info, $"winget {_version} found at {_executable}.");
                    return _executable;
                }
            }

            _log.Write(LogLevel.Warning, "winget was not found on PATH or in the WindowsApps alias folder.");
            return null;
        }
        finally
        {
            _discoveryLock.Release();
        }
    }

    public async Task<IReadOnlyList<WingetPackage>> ListInstalledAsync(CancellationToken cancellationToken = default)
    {
        if (await EnsureDiscoveredAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            return Array.Empty<WingetPackage>();
        }

        var result = await RunAsync(
            ["list", "--accept-source-agreements", "--disable-interactivity"],
            TimeSpan.FromMinutes(5),
            line => { },
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0 && string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            _log.Write(LogLevel.Warning, $"winget list failed ({result.ExitCode}): {result.StandardError.Trim()}");
            return Array.Empty<WingetPackage>();
        }

        var packages = WingetTableParser.ParseList(result.CombinedOutput);
        _log.Write(LogLevel.Info, $"winget reported {packages.Count} installed packages.");
        return packages;
    }

    public async Task<IReadOnlyList<WingetPackage>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || await EnsureDiscoveredAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            return Array.Empty<WingetPackage>();
        }

        var result = await RunAsync(
            ["search", "--query", query, "--accept-source-agreements", "--disable-interactivity"],
            TimeSpan.FromMinutes(2),
            _ => { },
            cancellationToken).ConfigureAwait(false);

        return WingetTableParser.ParseSearch(result.CombinedOutput);
    }

    public Task<ProcessResult> RunAsync(
        IEnumerable<string> arguments,
        TimeSpan timeout,
        Action<ProcessLine>? onLine,
        CancellationToken cancellationToken = default)
    {
        return _runner.RunAsync(
            Executable,
            arguments,
            new ProcessRunOptions { Timeout = timeout, OnLine = onLine },
            cancellationToken);
    }

    public Task<ProcessResult> InstallAsync(
        string packageId,
        string? source,
        Action<ProcessLine>? onLine,
        CancellationToken cancellationToken = default)
    {
        var args = new List<string>
        {
            "install",
            "--id", packageId,
            "--exact",
            "--silent",
            "--accept-package-agreements",
            "--accept-source-agreements",
            "--disable-interactivity"
        };
        if (!string.IsNullOrWhiteSpace(source))
        {
            args.Add("--source");
            args.Add(source);
        }

        return RunAsync(args, TimeSpan.FromMinutes(45), onLine, cancellationToken);
    }

    /// <summary>Apps winget can update, from <c>winget upgrade</c>. Pinned ones are marked, not dropped.</summary>
    public async Task<IReadOnlyList<CtrlZ.Core.AppUpdates.AppUpdate>> ListUpgradesAsync(CancellationToken cancellationToken = default)
    {
        if (await EnsureDiscoveredAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            return Array.Empty<CtrlZ.Core.AppUpdates.AppUpdate>();
        }

        var result = await RunAsync(
            ["upgrade", "--accept-source-agreements", "--disable-interactivity"],
            TimeSpan.FromMinutes(5),
            _ => { },
            cancellationToken).ConfigureAwait(false);

        var updates = CtrlZ.Core.AppUpdates.AppUpdateParser.Parse(result.CombinedOutput);
        _log.Write(LogLevel.Info, $"winget reported {updates.Count} available app updates.");
        return updates;
    }

    /// <summary>Updates one package by exact id, silently, accepting agreements like an install does.</summary>
    public Task<ProcessResult> UpgradeAsync(string packageId, Action<ProcessLine>? onLine, CancellationToken cancellationToken = default)
    {
        return RunAsync(
            ["upgrade", "--id", packageId, "--exact", "--silent", "--accept-package-agreements",
             "--accept-source-agreements", "--disable-interactivity"],
            TimeSpan.FromMinutes(45),
            onLine,
            cancellationToken);
    }

    public Task<ProcessResult> UpgradeAppInstallerAsync(Action<ProcessLine>? onLine, CancellationToken cancellationToken = default)
    {
        return RunAsync(
            ["upgrade", "--id", "Microsoft.AppInstaller", "--exact", "--silent", "--accept-package-agreements",
                "--accept-source-agreements", "--disable-interactivity"],
            TimeSpan.FromMinutes(15),
            onLine,
            cancellationToken);
    }

    private static IEnumerable<string> CandidatePaths()
    {
        yield return "winget";

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var alias = Path.Combine(local, "Microsoft", "WindowsApps", "winget.exe");
        if (File.Exists(alias))
        {
            yield return alias;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var packaged = Path.Combine(programFiles, "WindowsApps");
        if (Directory.Exists(packaged))
        {
            foreach (var file in Directory.EnumerateFiles(packaged, "winget.exe", SearchOption.AllDirectories).Take(3))
            {
                yield return file;
            }
        }
    }
}
