// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Logging;

namespace CtrlZ.Core.Updates;

/// <summary>
/// The portable <c>ctrlz.exe</c> next to a portable <c>CTRL+Z.exe</c>, replaced together with it.
/// Verified before anything moves, swapped with a <c>.old</c> backup, and either committed (backup
/// deleted) or restored together with the app. A companion that cannot be placed never blocks the
/// app's own update: the old command line stays and the log says so.
/// </summary>
public sealed class PortableCompanion
{
    private readonly string _downloaded;
    private readonly string _target;
    private readonly IAppLog _log;
    private bool _swapped;

    private PortableCompanion(string downloaded, string target, IAppLog log)
    {
        _downloaded = downloaded;
        _target = target;
        _log = log;
    }

    private string Backup => _target + ".old";

    /// <summary>
    /// Returns the companion to swap, or null when the request has none or its download is gone.
    /// Throws <see cref="UpdateVerificationException"/> when the download does not match its hash,
    /// before anything on disk has changed.
    /// </summary>
    public static async Task<PortableCompanion?> PrepareAsync(
        UpdateApplyRequest request, IAppLog log, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.CompanionDownloadedFile) ||
            string.IsNullOrWhiteSpace(request.CompanionTargetExe))
        {
            return null;
        }

        if (!File.Exists(request.CompanionDownloadedFile))
        {
            log.Write(LogLevel.Warning, "The downloaded command line is missing; ctrlz.exe is left as it was.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(request.CompanionExpectedSha256))
        {
            throw new UpdateVerificationException("The command line update has no SHA-256 to verify against.");
        }

        var actual = await UpdateVerifier.ComputeSha256Async(request.CompanionDownloadedFile, cancellationToken)
            .ConfigureAwait(false);
        UpdateVerifier.EnsureHash(request.CompanionExpectedSha256!, actual);

        return new PortableCompanion(request.CompanionDownloadedFile, request.CompanionTargetExe, log);
    }

    /// <summary>Moves the new command line into place. False (and nothing changed) when it cannot.</summary>
    public bool Swap()
    {
        try
        {
            TryDelete(Backup);
            if (File.Exists(_target))
            {
                File.Move(_target, Backup);
            }

            try
            {
                File.Move(_downloaded, _target);
            }
            catch
            {
                if (File.Exists(Backup))
                {
                    File.Move(Backup, _target);
                }

                throw;
            }

            _swapped = true;
            return true;
        }
        catch (Exception ex)
        {
            // For example ctrlz.exe is running in a terminal. The app still updates.
            _log.Write(LogLevel.Warning, "The command line could not be replaced; ctrlz.exe is left as it was.", ex);
            return false;
        }
    }

    /// <summary>Puts the previous command line back, together with the app's rollback.</summary>
    public void Restore()
    {
        if (!_swapped)
        {
            return;
        }

        try
        {
            if (File.Exists(Backup))
            {
                TryDelete(_target);
                File.Move(Backup, _target);
            }

            _swapped = false;
        }
        catch (Exception ex)
        {
            _log.Write(LogLevel.Error, $"The previous command line could not be restored. It is at '{Backup}'.", ex);
        }
    }

    /// <summary>The app passed its health window: drop the backup.</summary>
    public void Commit()
    {
        if (_swapped)
        {
            TryDelete(Backup);
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
            // Best effort; a locked file only leaves a stray .old.
        }
    }
}
