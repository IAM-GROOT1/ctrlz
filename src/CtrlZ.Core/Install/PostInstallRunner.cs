// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Localization;
using System.Diagnostics;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Install;

/// <summary>Outcome of one post-install command. Never carries the command text or its output.</summary>
public sealed record PostInstallResult(
    string AppName,
    int? ExitCode,
    TimeSpan Duration,
    bool TimedOut,
    bool Succeeded,
    string? Failure)
{
    /// <summary>Short label for the install report; deliberately free of arguments.</summary>
    public string Label => TimedOut ? "timed out" : Succeeded ? "ok" : "failed";
}

/// <summary>
/// Runs an approved post-install command. No shell, no elevation unless the command opts in, a hard
/// timeout that kills the whole process tree, and a log line that carries only the app name, exit
/// code, duration and whether it timed out.
/// </summary>
public sealed class PostInstallRunner
{
    private const int OutputCapChars = 8_192;

    private readonly IAppLog _log;

    public PostInstallRunner(IAppLog log)
    {
        _log = log;
    }

    public async Task<PostInstallResult> RunAsync(AppEntry app, PostInstallCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Executable))
        {
            return new PostInstallResult(app.Name, null, TimeSpan.Zero, false, false, CoreText.Get("PostInstallRunner_NoExecutable"));
        }

        var timeoutSeconds = Math.Clamp(command.TimeoutSeconds <= 0 ? 60 : command.TimeoutSeconds, 5, 600);
        var startInfo = new ProcessStartInfo
        {
            FileName = command.Executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (!string.IsNullOrWhiteSpace(command.WorkingDirectory))
        {
            startInfo.WorkingDirectory = command.WorkingDirectory;
        }

        if (command.RequiresElevation)
        {
            startInfo.Verb = "runas";
            startInfo.UseShellExecute = true;
            startInfo.RedirectStandardOutput = false;
            startInfo.RedirectStandardError = false;
        }

        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = startInfo };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            if (!process.Start())
            {
                return new PostInstallResult(app.Name, null, stopwatch.Elapsed, false, false, CoreText.Get("PostInstallRunner_CouldNotStart"));
            }

            var stdout = startInfo.RedirectStandardOutput
                ? ReadCappedAsync(process.StandardOutput)
                : Task.FromResult(string.Empty);
            var stderr = startInfo.RedirectStandardError
                ? ReadCappedAsync(process.StandardError)
                : Task.FromResult(string.Empty);

            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (Exception)
                    {
                        // Already gone.
                    }
                }

                stopwatch.Stop();
                _log.Write(
                    LogLevel.Warning,
                    $"Post-install for '{app.Name}': timed out after {stopwatch.Elapsed.TotalSeconds:F0}s; process tree killed.");

                return new PostInstallResult(app.Name, null, stopwatch.Elapsed, true, false, CoreText.Get("PostInstallRunner_TimedOut2"));
            }

            stopwatch.Stop();
            var exitCode = process.ExitCode;

            _log.Write(
                exitCode == 0 ? LogLevel.Info : LogLevel.Warning,
                $"Post-install for '{app.Name}': exit={exitCode} duration={stopwatch.ElapsedMilliseconds}ms timedOut=false");

            if (command.RequiresElevation)
            {
                // Output cannot be captured through a UAC launch, and that is the trade-off of opting in.
                return new PostInstallResult(app.Name, exitCode, stopwatch.Elapsed, false, exitCode == 0,
                    exitCode == 0 ? null : CoreText.Format("PostInstallRunner_ExitCode", exitCode));
            }

            var output = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
            if (!string.IsNullOrWhiteSpace(output))
            {
                // Off by default: the output may contain anything, including secrets a script echoed.
                _log.Write(LogLevel.Debug, $"Post-install output suppressed for '{app.Name}' ({output.Length} chars).");
            }

            return new PostInstallResult(app.Name, exitCode, stopwatch.Elapsed, false, exitCode == 0,
                exitCode == 0 ? null : CoreText.Format("PostInstallRunner_ExitCode", exitCode));
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _log.Write(LogLevel.Warning, $"Post-install for '{app.Name}' could not run: {ex.GetType().Name}.");
            return new PostInstallResult(app.Name, null, stopwatch.Elapsed, false, false, CoreText.Get("PostInstallRunner_CouldNotStart"));
        }
    }

    private static async Task<string> ReadCappedAsync(StreamReader reader)
    {
        var buffer = new char[OutputCapChars];
        var read = await reader.ReadBlockAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
        return new string(buffer, 0, read);
    }
}
