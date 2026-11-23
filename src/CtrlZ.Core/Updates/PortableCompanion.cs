// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Logging;

namespace CtrlZ.Core.Updates;

/// <summary>
/// The portable <c>ctrlz.exe</c> next to a portable <c>CTRL+Z.exe</c>, replaced together with it.
/// <para>
/// The target is always <c>ctrlz.exe</c> in the app's own folder, never a path from the request
/// file. The download is first moved next to it as <c>ctrlz.exe.new</c> and verified there, so
/// the bytes that get renamed into place are the bytes that were hashed. It is then swapped with a
/// <c>.old</c> backup and either committed (backup removed) or restored together with the app. A
/// hash mismatch stops the whole update before anything moves; any other problem only skips the
/// command line and the app still updates.
/// </para>
/// </summary>
public sealed class PortableCompanion
{
    public const string FileName = "ctrlz.exe";

    private readonly string _staged;
    private readonly string _target;
    private readonly IAppLog _log;
    private bool _backupMade;
    private bool _swapped;

    private PortableCompanion(string staged, string target, IAppLog log)
    {
        _staged = staged;
        _target = target;
        _log = log;
    }

    private string Backup => _target + ".old";

    /// <summary>The <c>ctrlz.exe</c> beside the app.</summary>
    public string TargetFile => _target;

    /// <summary>The previous command line after a swap, kept for a later rollback; null otherwise.</summary>
    public string? KeptBackup => _swapped ? Backup : null;

    /// <summary>
    /// Returns the companion to swap, or null when there is none or it cannot be prepared.
    /// Throws <see cref="UpdateVerificationException"/> on a hash mismatch, before any file of the
    /// app or the command line has changed.
    /// </summary>
    public static async Task<PortableCompanion?> PrepareAsync(
        UpdateApplyRequest request, IAppLog log, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.CompanionDownloadedFile) || string.IsNullOrWhiteSpace(request.TargetExe))
        {
            return null;
        }

        var folder = Path.GetDirectoryName(request.TargetExe);
        if (string.IsNullOrEmpty(folder))
        {
            return null;
        }

        // Only an existing command line beside the app is ever replaced.
        var target = Path.Combine(folder, FileName);
        if (!File.Exists(target))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(request.CompanionExpectedSha256))
        {
            throw new UpdateVerificationException("The command line update has no SHA-256 to verify against.");
        }

        var staged = target + ".new";
        var verified = false;
        try
        {
            if (!File.Exists(request.CompanionDownloadedFile))
            {
                log.Write(LogLevel.Warning, "The downloaded command line is missing; ctrlz.exe is left as it was.");
                return null;
            }

            string actual;
            try
            {
                File.Move(request.CompanionDownloadedFile, staged, overwrite: true);
                actual = await UpdateVerifier.ComputeSha256Async(staged, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Write(LogLevel.Warning, "The command line update could not be prepared; ctrlz.exe is left as it was.", ex);
                return null;
            }

            UpdateVerifier.EnsureHash(request.CompanionExpectedSha256!, actual);
            verified = true;
            return new PortableCompanion(staged, target, log);
        }
        finally
        {
            // Nothing unverified stays beside the app, whatever ended the preparation.
            if (!verified)
            {
                TryDelete(staged);
            }
        }
    }

    /// <summary>Renames the verified command line into place. False when it could not.</summary>
    public bool Swap()
    {
        try
        {
            File.Move(_target, Backup, overwrite: true);
            _backupMade = true;
            File.Move(_staged, _target);
            _swapped = true;
            return true;
        }
        catch (Exception ex)
        {
            _log.Write(LogLevel.Warning, "The command line could not be replaced; putting the previous one back.", ex);
            PutBackupBack();
            TryDelete(_staged);
            return false;
        }
    }

    /// <summary>Puts the previous command line back, together with the app's rollback.</summary>
    public void Restore()
    {
        if (_swapped || _backupMade)
        {
            PutBackupBack();
        }

        TryDelete(_staged);
    }

    /// <summary>The app passed its health window: drop the backup.</summary>
    public void Commit()
    {
        if (_swapped)
        {
            // A ctrlz.exe that was running keeps its old file locked as .old; the next update
            // replaces that leftover.
            TryDelete(Backup);
            _backupMade = false;
        }
    }

    private void PutBackupBack()
    {
        if (!_backupMade)
        {
            return;
        }

        try
        {
            File.Move(Backup, _target, overwrite: true);
            _backupMade = false;
            _swapped = false;
        }
        catch (Exception ex)
        {
            _log.Write(LogLevel.Error, $"The previous command line could not be put back; it is kept as {FileName}.old next to the app.", ex);
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
            // Best effort.
        }
    }
}
