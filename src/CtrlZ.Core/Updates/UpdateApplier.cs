using System.Diagnostics;
using CtrlZ.Core.Logging;

namespace CtrlZ.Core.Updates;

/// <summary>
/// Applies a downloaded update in the helper process, after the running app has exited.
/// <para>
/// Portable copies are replaced in place: the old exe is moved to <c>.old</c>, the new exe
/// is moved in and started, and the backup is only deleted once the new build has survived a
/// short health window. Any failure restores the backup. Installed copies run the new
/// Setup.exe silently and are relaunched by this helper, because the installer's
/// <c>[Run]</c> entry is skipped in silent mode.
/// </para>
/// </summary>
public sealed class UpdateApplier
{
    private static readonly TimeSpan ProcessExitTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HealthWindow = TimeSpan.FromSeconds(12);

    private readonly IAppLog _log;

    public UpdateApplier(IAppLog log)
    {
        _log = log;
    }

    public async Task<UpdateApplyResult> ApplyAsync(UpdateApplyRequest request, CancellationToken cancellationToken = default)
    {
        UpdateApplyResult result;
        try
        {
            _log.Write(LogLevel.Info, $"Applying {request.Kind} update to {request.Version ?? "unknown version"}.");

            if (request.WaitForProcessId > 0)
            {
                await WaitForProcessExitAsync(request.WaitForProcessId, ProcessExitTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }

            result = request.Kind.Equals(nameof(UpdateInstallKind.Installed), StringComparison.OrdinalIgnoreCase)
                ? await ApplyInstalledAsync(request, cancellationToken).ConfigureAwait(false)
                : await ApplyPortableAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Write(LogLevel.Error, "The update could not be applied.", ex);
            result = UpdateApplyResult.Failed("The update could not be applied: " + ex.Message);
        }

        if (!result.Success)
        {
            // Never leave the user without an app. On a rollback this starts the restored
            // previous build; otherwise it starts whatever is still on disk.
            TryStart(request.TargetExe);
        }

        return result;
    }

    private async Task<UpdateApplyResult> ApplyPortableAsync(UpdateApplyRequest request, CancellationToken cancellationToken)
    {
        if (!File.Exists(request.DownloadedFile))
        {
            return UpdateApplyResult.Failed("The downloaded update is missing. Nothing was changed.");
        }

        if (!string.IsNullOrWhiteSpace(request.ExpectedSha256))
        {
            var actual = await UpdateVerifier.ComputeSha256Async(request.DownloadedFile, cancellationToken)
                .ConfigureAwait(false);
            UpdateVerifier.EnsureHash(request.ExpectedSha256!, actual);
        }

        var target = request.TargetExe;
        var backup = target + ".old";
        TryDelete(backup);
        File.Move(target, backup);

        try
        {
            File.Move(request.DownloadedFile, target);
        }
        catch (Exception ex)
        {
            _log.Write(LogLevel.Error, "Could not place the new executable in the target folder.", ex);
            Restore(backup, target);
            return UpdateApplyResult.Failed("The new version could not be put in place. The previous version was restored.");
        }

        var started = TryStart(target);
        if (started is null)
        {
            Restore(backup, target);
            return UpdateApplyResult.Failed("The new version could not be started. The previous version was restored.");
        }

        if (!await WaitForHealthAsync(started, HealthWindow, cancellationToken).ConfigureAwait(false))
        {
            _log.Write(LogLevel.Error, "The updated build exited during its startup window; rolling back.");
            try
            {
                if (!started.HasExited)
                {
                    started.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Already gone.
            }

            Restore(backup, target);
            return UpdateApplyResult.Failed(
                "The new version closed unexpectedly, so CTRL+Z rolled back to the previous version.");
        }

        TryDelete(backup);
        _log.Write(LogLevel.Info, $"Update to {request.Version} applied and the previous build was removed.");
        return UpdateApplyResult.Ok(request.Version);
    }

    private async Task<UpdateApplyResult> ApplyInstalledAsync(UpdateApplyRequest request, CancellationToken cancellationToken)
    {
        if (!File.Exists(request.DownloadedFile))
        {
            return UpdateApplyResult.Failed("The downloaded installer is missing. Nothing was changed.");
        }

        // /SILENT without /VERYSILENT still shows progress; /NORESTART avoids a reboot prompt;
        // /CLOSEAPPLICATIONS is harmless because the app has already exited. The installer's
        // own [Run] entry is skipped in silent mode, so this helper relaunches the app.
        var startInfo = new ProcessStartInfo(request.DownloadedFile)
        {
            UseShellExecute = true,
            Arguments = "/SILENT /NORESTART /CLOSEAPPLICATIONS /NOCANCEL",
            WorkingDirectory = Path.GetDirectoryName(request.DownloadedFile) ?? Environment.CurrentDirectory
        };

        using var installer = Process.Start(startInfo);
        if (installer is null)
        {
            return UpdateApplyResult.Failed("The installer could not be started. Your previous version is still installed.");
        }

        await installer.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (installer.ExitCode != 0)
        {
            _log.Write(LogLevel.Error, $"The installer exited with code {installer.ExitCode}.");
            return UpdateApplyResult.Failed(
                $"The installer reported an error (code {installer.ExitCode}). Your previous version is still installed.");
        }

        TryStart(request.TargetExe);
        return UpdateApplyResult.Ok(request.Version);
    }

    /// <summary>Starts an executable detached; returns null when it could not be started.</summary>
    public static Process? TryStart(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            return null;
        }

        try
        {
            var startInfo = new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory
            };

            return Process.Start(startInfo);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> WaitForHealthAsync(Process process, TimeSpan window, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + window;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
            try
            {
                if (process.HasExited)
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }
        }

        return true;
    }

    private static async Task WaitForProcessExitAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // The process is already gone.
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timed out waiting: proceed rather than leaving the user without an app.
        }
    }

    private void Restore(string backup, string target)
    {
        try
        {
            if (File.Exists(backup))
            {
                TryDelete(target);
                File.Move(backup, target);
                _log.Write(LogLevel.Warning, "The previous version was restored.");
            }
        }
        catch (Exception ex)
        {
            _log.Write(LogLevel.Error, $"Rollback failed. The previous build is at '{backup}'.", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort; a locked file only leaves a stray .old next to the app.
        }
    }
}
