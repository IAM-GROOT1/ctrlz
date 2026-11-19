// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Diagnostics;
using System.Text;

namespace CtrlZ.Core.Infrastructure;

public sealed record ProcessLine(string Text, bool IsError);

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut)
{
    public string CombinedOutput => string.Join(Environment.NewLine,
        new[] { StandardOutput, StandardError }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

public sealed class ProcessRunOptions
{
    public string? WorkingDirectory { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(30);
    public Action<ProcessLine>? OnLine { get; init; }
    public Encoding Encoding { get; init; } = new UTF8Encoding(false);
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
}

/// <summary>
/// Streams child-process output without a shell. Keeping this small and central means
/// every external tool call gets the same encoding, cancellation and timeout behaviour.
/// </summary>
public sealed class ProcessRunner
{
    public Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        ProcessRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return RunCoreAsync(fileName, arguments, null, options, cancellationToken);
    }

    /// <summary>
    /// Runs with a pre-built Windows command line. Installers frequently emit switches
    /// containing embedded quotes (InstallShield's /v"/qn"), which must stay intact.
    /// </summary>
    public Task<ProcessResult> RunCommandLineAsync(
        string fileName,
        string commandLine,
        ProcessRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return RunCoreAsync(fileName, null, commandLine, options, cancellationToken);
    }

    private async Task<ProcessResult> RunCoreAsync(
        string fileName,
        IEnumerable<string>? arguments,
        string? commandLine,
        ProcessRunOptions? options,
        CancellationToken cancellationToken)
    {
        options ??= new ProcessRunOptions();
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = options.Encoding,
            StandardErrorEncoding = options.Encoding
        };

        if (!string.IsNullOrWhiteSpace(options.WorkingDirectory))
        {
            startInfo.WorkingDirectory = options.WorkingDirectory;
        }

        if (commandLine is not null)
        {
            startInfo.Arguments = commandLine;
        }
        else if (arguments is not null)
        {
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        if (options.Environment is not null)
        {
            foreach (var pair in options.Environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var outputGate = new object();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (outputGate) { stdout.AppendLine(e.Data); }
            TryReport(options, e.Data, isError: false);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (outputGate) { stderr.AppendLine(e.Data); }
            TryReport(options, e.Data, isError: true);
        };
        process.Exited += (_, _) => completion.TrySetResult();

        try
        {
            if (!process.Start())
            {
                return new ProcessResult(-1, string.Empty, $"Could not start {fileName}.", false);
            }
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, string.Empty, ex.Message, false);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(options.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var timedOut = false;
        try
        {
            await completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            // DataReceived can fire just after Exited; this short wait flushes buffered lines.
            process.WaitForExit(2000);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // The process may have exited between the check and kill.
            }

            try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
        }

        string output;
        string error;
        lock (outputGate)
        {
            output = stdout.ToString();
            error = stderr.ToString();
        }

        int exitCode;
        try { exitCode = process.HasExited ? process.ExitCode : -1; }
        catch { exitCode = -1; }

        if (timedOut)
        {
            error += $"{Environment.NewLine}Process timed out after {options.Timeout.TotalMinutes:0.#} minutes.";
        }

        return new ProcessResult(exitCode, output, error, timedOut);
    }

    private static void TryReport(ProcessRunOptions options, string line, bool isError)
    {
        try { options.OnLine?.Invoke(new ProcessLine(line, isError)); }
        catch
        {
            // A logging/UI callback must not terminate the process pump.
        }
    }
}
