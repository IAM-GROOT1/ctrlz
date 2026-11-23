// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Diagnostics;
using CtrlZ.Core.Logging;

namespace CtrlZ.Core.Updates;

/// <summary>
/// Applies a downloaded update in the helper process, after the running app has exited.
/// <para>
/// Portable copies are replaced in place: the old exe is moved to <c>.old</c>, the new exe
/// is moved in and started, and this helper waits until the new build shows its main window.
/// If it exits first, the backup is restored. The backup is kept until the new build has shown
/// its window on <see cref="UpdateProbation.LaunchesToCommit"/> launches (see
/// <see cref="UpdateProbation"/>), so a build that fails on a later start can still be rolled
/// back. Installed copies run the new
/// Setup.exe silently and are relaunched by this helper, because the installer's
/// <c>[Run]</c> entry is skipped in silent mode.
/// </para>
/// </summary>
public sealed class UpdateApplier
{
    private static readonly TimeSpan ProcessExitTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long the helper waits for the new build's main window. A build still running without a
    /// window after this is left alone; its next start decides (see <see cref="UpdateProbation"/>).
    /// </summary>
    private static readonly TimeSpan LoadWindow = TimeSpan.FromSeconds(90);

    private readonly IAppLog _log;
    private readonly UpdateProbation _probation;

    public UpdateApplier(IAppLog log, UpdateProbation probation)
    {
        _log = log;
        _probation = probation;
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

        // Verified before anything on disk changes; a bad companion stops the whole update.
        var companion = await PortableCompanion.PrepareAsync(request, _log, cancellationToken).ConfigureAwait(false);

        var target = request.TargetExe;
        var backup = UpdateProbation.BackupPath(target);
        TryDelete(backup);
        File.Move(target, backup);

        try
        {
            File.Move(request.DownloadedFile, target);
        }
        catch (Exception ex)
        {
            _log.Write(LogLevel.Error, "Could not place the new executable in the target folder.", ex);
            companion?.Restore();
            Restore(backup, target);
            return UpdateApplyResult.Failed("The new version could not be put in place. The previous version was restored.");
        }

        companion?.Swap();

        // Written before the new build starts, so its first launch is already being watched.
        // Without the record nothing would ever remove the backup or roll back, so a failure here
        // undoes the swap.
        try
        {
            _probation.Begin(target, request.Version, companion?.KeptBackup is not null);
        }
        catch (Exception ex)
        {
            _log.Write(LogLevel.Error, "Could not record the update probation; restoring the previous version.", ex);
            companion?.Restore();
            Restore(backup, target);
            return UpdateApplyResult.Failed("The update could not be recorded. The previous version was restored.");
        }

        var started = TryStart(target);
        if (started is null)
        {
            _probation.Clear();
            companion?.Restore();
            Restore(backup, target);
            return UpdateApplyResult.Failed("The new version could not be started. The previous version was restored.");
        }

        var health = await WaitForMainWindowAsync(started, LoadWindow, cancellationToken).ConfigureAwait(false);
        if (health == StartupHealth.Exited)
        {
            _log.Write(LogLevel.Error, "The updated build exited before showing its main window; rolling back.");
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

            _probation.Clear();
            companion?.Restore();
            Restore(backup, target);
            return UpdateApplyResult.Failed(
                "The new version closed unexpectedly, so CTRL+Z rolled back to the previous version.");
        }

        _log.Write(LogLevel.Info, health == StartupHealth.Loaded
            ? $"Update to {request.Version} showed its main window; the previous build is kept until it has loaded {UpdateProbation.LaunchesToCommit} times."
            : $"Update to {request.Version} is still starting after {LoadWindow.TotalSeconds:0} s; its next start decides whether to keep it.");
        return UpdateApplyResult.Ok(request.Version);
    }

    private enum StartupHealth
    {
        Loaded,
        Exited,
        StillStarting
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

    /// <summary>Waits until the new build reports its main window, exits, or the window passes.</summary>
    private async Task<StartupHealth> WaitForMainWindowAsync(Process process, TimeSpan window, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + window;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
            if (HasLoaded())
            {
                return StartupHealth.Loaded;
            }

            bool exited;
            try
            {
                exited = process.HasExited;
            }
            catch
            {
                exited = true;
            }

            if (exited)
            {
                // It may have shown its window and been closed between two checks.
                return HasLoaded() ? StartupHealth.Loaded : StartupHealth.Exited;
            }
        }

        return StartupHealth.StillStarting;
    }

    private bool HasLoaded() => _probation.Read() is { LoadedLaunches: > 0 };

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
