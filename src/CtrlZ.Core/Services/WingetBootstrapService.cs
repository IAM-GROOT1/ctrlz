// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Text.RegularExpressions;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Logging;

namespace CtrlZ.Core.Services;

public sealed class WingetBootstrapException : Exception
{
    public WingetBootstrapException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Makes winget usable on a clean Windows install. Normally winget is already present on
/// Windows 11; the fallback downloads App Installer and registers the MSIX bundle.
/// </summary>
public sealed partial class WingetBootstrapService
{
    private static readonly Version MinimumRecommendedVersion = new(1, 6);
    private readonly WingetCli _winget;
    private readonly DownloadService _downloads;
    private readonly ProcessRunner _runner;
    private readonly AppPaths _paths;
    private readonly IAppLog _log;

    public WingetBootstrapService(WingetCli winget, DownloadService downloads, ProcessRunner runner, AppPaths paths, IAppLog log)
    {
        _winget = winget;
        _downloads = downloads;
        _runner = runner;
        _paths = paths;
        _log = log;
    }

    [GeneratedRegex(@"(\d+)\.(\d+)(?:\.(\d+))?")]
    private static partial Regex VersionRegex();

    public async Task<bool> EnsureReadyAsync(Action<string> log, CancellationToken cancellationToken = default)
    {
        if (await _winget.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            log($"winget {_winget.Version} is available.");
            if (TryParseVersion(_winget.Version, out var version) && version < MinimumRecommendedVersion)
            {
                log($"winget {_winget.Version} is older than the recommended {MinimumRecommendedVersion}. Trying to update App Installer…");
                var update = await _winget.UpgradeAppInstallerAsync(
                    line => { if (!string.IsNullOrWhiteSpace(line.Text)) log(line.Text.Trim()); },
                    cancellationToken).ConfigureAwait(false);
                if (update.ExitCode == 0)
                {
                    _winget.ResetDiscovery();
                    await _winget.EnsureDiscoveredAsync(cancellationToken).ConfigureAwait(false);
                    log("winget update completed.");
                }
                else
                {
                    log("winget could not update itself automatically. The existing version will still be used.");
                }
            }

            return true;
        }

        log("winget is missing. Downloading Microsoft App Installer…");
        Directory.CreateDirectory(_paths.Downloads);
        try
        {
            var download = await _downloads.DownloadAsync(
                "https://aka.ms/getwinget",
                "Microsoft.DesktopAppInstaller.msixbundle",
                expectedSha256: null,
                progress: null,
                cancellationToken).ConfigureAwait(false);

            if (download.Size < 512 * 1024)
            {
                throw new WingetBootstrapException(
                    "The App Installer download was unexpectedly small. Download it manually from https://aka.ms/getwinget.");
            }

            log($"Downloaded App Installer ({FileUtil.FormatBytes(download.Size)}). Registering it…");
            var script = $"Add-AppxPackage -Path '{download.Path.Replace("'", "''")}' -ForceApplicationShutdown";
            var result = await _runner.RunAsync(
                "powershell.exe",
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script],
                new ProcessRunOptions
                {
                    Timeout = TimeSpan.FromMinutes(10),
                    OnLine = line => { if (!string.IsNullOrWhiteSpace(line.Text)) log(line.Text.Trim()); }
                },
                cancellationToken).ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                throw new WingetBootstrapException(
                    "App Installer could not be registered. Install it manually from https://aka.ms/getwinget, then retry. " +
                    LastError(result));
            }

            _winget.ResetDiscovery();
            if (await _winget.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
            {
                log($"winget {_winget.Version} is ready.");
                return true;
            }

            throw new WingetBootstrapException(
                "App Installer was installed, but winget is not visible yet. Sign out and back in, or install it from https://aka.ms/getwinget.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (WingetBootstrapException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Write(LogLevel.Error, "winget bootstrap failed.", ex);
            throw new WingetBootstrapException(
                "winget could not be installed automatically. Install App Installer from https://aka.ms/getwinget, then retry. " +
                ex.Message, ex);
        }
    }

    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = VersionRegex().Match(text);
        if (!match.Success)
        {
            return false;
        }

        var major = int.Parse(match.Groups[1].Value);
        var minor = int.Parse(match.Groups[2].Value);
        var build = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;
        version = new Version(major, minor, build);
        return true;
    }

    private static string LastError(ProcessResult result)
    {
        var text = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(' ', lines.TakeLast(3));
    }
}
