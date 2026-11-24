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
/// back. Installed copies run the new Setup.exe silently and are relaunched by this helper,
/// because the installer's <c>[Run]</c> entry is skipped in silent mode. Before the installer
/// runs, the current exe is copied to <c>.old</c>, so the same probation can put it back if the
/// new build fails to start.
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
            companion?.Restore();
            if (Restore(backup, target))
            {
                _probation.Clear();
            }

            return UpdateApplyResult.RolledBackFrom(
                "The new version could not be started. The previous version was restored.", request.Version);
        }

        using (started)
        {
            var health = await WaitForMainWindowAsync(started, LoadWindow, cancellationToken).ConfigureAwait(false);
            if (health == StartupHealth.Exited)
            {
                _log.Write(LogLevel.Error, "The updated build exited before showing its main window; rolling back.");
                Stop(started);
                companion?.Restore();
                // The record stays when the restore fails, so the next start of the new build rolls back.
                if (Restore(backup, target))
                {
                    _probation.Clear();
                }

                return UpdateApplyResult.RolledBackFrom(
                    "The new version closed unexpectedly, so CTRL+Z rolled back to the previous version.", request.Version);
            }

            LogHealth(request.Version, health);
        }

        return UpdateApplyResult.Ok(request.Version);
    }

    private void LogHealth(string? version, StartupHealth health) =>
        _log.Write(LogLevel.Info, health == StartupHealth.Loaded
            ? $"Update to {version} showed its main window; the previous build is kept until it has loaded {UpdateProbation.LaunchesToCommit} times."
            : $"Update to {version} is still starting after {LoadWindow.TotalSeconds:0} s; its next start decides whether to keep it.");

    /// <summary>Ends a build that failed to start and waits until its file is no longer in use.</summary>
    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            process.WaitForExit(10_000);
        }
        catch
        {
            // Already gone.
        }
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

        // A copy of the current build, so a new version that fails to start can be undone the same
        // way as a portable update. Copied, not moved: the installer replaces the files in place.
        var target = request.TargetExe;
        var backedUp = TryKeepInstalledBuild(target, out var companionBackedUp);

        Process? launched;
        try
        {
            launched = Process.Start(startInfo);
        }
        catch
        {
            // For example a declined UAC prompt or an installer blocked by antivirus.
            DropInstalledBackup(target, companionBackedUp);
            throw;
        }

        using var installer = launched;
        if (installer is null)
        {
            DropInstalledBackup(target, companionBackedUp);
            return UpdateApplyResult.Failed("The installer could not be started. Your previous version is still installed.");
        }

        await installer.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (installer.ExitCode != 0)
        {
            // Inno Setup undoes its own changes when it fails, so the copy is not needed.
            DropInstalledBackup(target, companionBackedUp);
            _log.Write(LogLevel.Error, $"The installer exited with code {installer.ExitCode}.");
            return UpdateApplyResult.Failed(
                $"The installer reported an error (code {installer.ExitCode}). Your previous version is still installed.");
        }

        if (!backedUp)
        {
            TryStart(target);
            return UpdateApplyResult.Ok(request.Version);
        }

        try
        {
            _probation.Begin(target, request.Version, companionBackedUp);
        }
        catch (Exception ex)
        {
            // The update itself succeeded; it just cannot be watched. Keep it, drop the copy.
            _log.Write(LogLevel.Warning, "Could not record the update probation; the new version is kept without a rollback copy.", ex);
            DropInstalledBackup(target, companionBackedUp);
            TryStart(target);
            return UpdateApplyResult.Ok(request.Version);
        }

        var started = TryStart(target);
        if (started is null)
        {
            if (RestoreInstalled(target, companionBackedUp))
            {
                _probation.Clear();
            }

            return UpdateApplyResult.RolledBackFrom(
                "The new version could not be started. The previous version was restored.", request.Version);
        }

        using (started)
        {
            var health = await WaitForMainWindowAsync(started, LoadWindow, cancellationToken).ConfigureAwait(false);
            if (health == StartupHealth.Exited)
            {
                _log.Write(LogLevel.Error, "The updated build exited before showing its main window; rolling back.");
                Stop(started);
                // The record stays when the restore fails, so the next start of the new build rolls back.
                if (RestoreInstalled(target, companionBackedUp))
                {
                    _probation.Clear();
                }

                return UpdateApplyResult.RolledBackFrom(
                    "The new version closed unexpectedly, so CTRL+Z rolled back to the previous version.", request.Version);
            }

            LogHealth(request.Version, health);
        }

        return UpdateApplyResult.Ok(request.Version);
    }

    /// <summary>
    /// Copies the installed <c>CTRL+Z.exe</c> (and <c>ctrlz.exe</c> when present) to <c>.old</c>
    /// beside it. Returns false when the folder cannot be written, for example a machine-wide
    /// install updated without administrator rights; the update then runs without a rollback copy.
    /// </summary>
    private bool TryKeepInstalledBuild(string? target, out bool companionBackedUp)
    {
        companionBackedUp = false;
        if (string.IsNullOrWhiteSpace(target) || !File.Exists(target))
        {
            return false;
        }

        try
        {
            // Removed first and then created anew, so a link planted at the .old path is never
            // followed by the copy.
            CopyFresh(target, UpdateProbation.BackupPath(target));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Write(LogLevel.Warning, $"No rollback copy of the installed build could be made: {LogPrivacy.Describe(ex)}");
            return false;
        }

        var companion = UpdateProbation.CompanionPath(target);
        if (File.Exists(companion))
        {
            try
            {
                CopyFresh(companion, UpdateProbation.BackupPath(companion));
                companionBackedUp = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Write(LogLevel.Warning, $"No rollback copy of the command line could be made: {LogPrivacy.Describe(ex)}");
            }
        }

        return true;
    }

    private static void CopyFresh(string source, string destination)
    {
        if (File.Exists(destination))
        {
            File.Delete(destination);
        }

        File.Copy(source, destination, overwrite: false);
    }

    /// <summary>True when the app itself was put back; the command line is best effort.</summary>
    private bool RestoreInstalled(string target, bool companionBackedUp)
    {
        var restored = Restore(UpdateProbation.BackupPath(target), target);
        if (companionBackedUp)
        {
            var companion = UpdateProbation.CompanionPath(target);
            Restore(UpdateProbation.BackupPath(companion), companion);
        }

        return restored;
    }

    private static void DropInstalledBackup(string? target, bool companionBackedUp)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        TryDelete(UpdateProbation.BackupPath(target));
        if (companionBackedUp)
        {
            TryDelete(UpdateProbation.BackupPath(UpdateProbation.CompanionPath(target)));
        }
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

    /// <summary>
    /// Puts the previous build back in one step (a rename that replaces the target), so there is
    /// never a moment without an exe at <paramref name="target"/>. Returns false when it failed.
    /// </summary>
    private bool Restore(string backup, string target)
    {
        try
        {
            if (!File.Exists(backup))
            {
                return false;
            }

            File.Move(backup, target, overwrite: true);
            _log.Write(LogLevel.Warning, "The previous version was restored.");
            return true;
        }
        catch (Exception ex)
        {
            _log.Write(LogLevel.Error, $"Rollback failed. The previous build is kept as {Path.GetFileName(backup)} next to the app.", ex);
            return false;
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
