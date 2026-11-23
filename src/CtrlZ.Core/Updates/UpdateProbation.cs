// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Logging;

namespace CtrlZ.Core.Updates;

/// <summary>
/// A portable update that has not yet proved itself; stored as state/update-probation.json.
/// <para>
/// The record holds no file paths CTRL+Z acts on. The previous build and the command line are
/// always found beside the running exe (<c>CTRL+Z.exe.old</c>, <c>ctrlz.exe</c>,
/// <c>ctrlz.exe.old</c>), so an edited record cannot make CTRL+Z move, delete or start any other file.
/// </para>
/// </summary>
public sealed class UpdateProbationRecord
{
    public string? Version { get; set; }

    /// <summary>The updated <c>CTRL+Z.exe</c>; a record for any other exe is ignored.</summary>
    public string TargetExe { get; set; } = string.Empty;

    /// <summary>True when <c>ctrlz.exe</c> was replaced together with the app.</summary>
    public bool CompanionSwapped { get; set; }

    /// <summary>Launches of the new build that reached its main window.</summary>
    public int LoadedLaunches { get; set; }

    /// <summary>The launch that has started but not yet shown its main window.</summary>
    public int? LaunchPid { get; set; }

    public DateTime? LaunchStartedUtc { get; set; }
}

public enum ProbationStartup
{
    /// <summary>No update is on probation for this exe.</summary>
    None,

    /// <summary>This launch is being watched; call <see cref="UpdateProbation.MarkLoaded"/> once the main window shows.</summary>
    Watching,

    /// <summary>
    /// The last launch of the new build never showed its main window, so the previous build was put
    /// back at the same path. The caller should start that path and exit.
    /// </summary>
    RolledBack
}

/// <summary>
/// Keeps the previous portable build until the new one has shown its main window on
/// <see cref="LaunchesToCommit"/> launches. A launch that starts but never shows its window is
/// noticed on the next start, which puts the previous build back.
/// </summary>
public sealed class UpdateProbation
{
    public const string FileName = "update-probation.json";
    public const int LaunchesToCommit = 2;

    private readonly string _path;
    private readonly IAppLog _log;
    private readonly Func<int, DateTime?, bool> _isRunning;

    public UpdateProbation(string path, IAppLog log, Func<int, DateTime?, bool>? isRunning = null)
    {
        _path = path;
        _log = log;
        _isRunning = isRunning ?? IsRunning;
    }

    /// <summary>The previous build beside <paramref name="exe"/>.</summary>
    public static string BackupPath(string exe) => exe + ".old";

    /// <summary>The command line beside <paramref name="exe"/>.</summary>
    public static string CompanionPath(string exe) =>
        Path.Combine(Path.GetDirectoryName(exe) ?? string.Empty, PortableCompanion.FileName);

    public static string FailedPath(string exe) => exe + ".failed";

    public UpdateProbationRecord? Read()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return File.Exists(_path)
                    ? JsonSerializer.Deserialize<UpdateProbationRecord>(File.ReadAllText(_path), JsonDefaults.Compact)
                    : null;
            }
            catch (IOException) when (attempt < 4)
            {
                // The helper and the app both touch the file; a short wait clears a sharing clash.
                Thread.Sleep(100);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _log.Write(LogLevel.Warning, $"The update probation record could not be read: {LogPrivacy.Describe(ex)}");
                return null;
            }
        }
    }

    /// <summary>Written by the update helper before it starts the new build.</summary>
    public void Begin(string targetExe, string? version, bool companionSwapped)
    {
        Save(new UpdateProbationRecord
        {
            Version = version,
            TargetExe = targetExe,
            CompanionSwapped = companionSwapped
        });
    }

    /// <summary>Ends the probation without touching any file but the record.</summary>
    public void Clear()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Write(LogLevel.Warning, $"The update probation record could not be removed: {LogPrivacy.Describe(ex)}");
        }
    }

    /// <summary>Called when the app starts, before any window is built.</summary>
    public ProbationStartup OnLaunch(string currentExe, int processId, DateTime processStartUtc)
    {
        var record = Read();
        if (record is null || !SamePath(record.TargetExe, currentExe))
        {
            return ProbationStartup.None;
        }

        if (!File.Exists(BackupPath(currentExe)))
        {
            _log.Write(LogLevel.Warning, "The previous build kept for rollback is gone; ending the update probation.");
            Clear();
            return ProbationStartup.None;
        }

        if (record.LaunchPid is { } earlier && earlier != processId)
        {
            if (_isRunning(earlier, record.LaunchStartedUtc))
            {
                // Another copy is still starting; it reports for itself.
                return ProbationStartup.None;
            }

            _log.Write(LogLevel.Error,
                $"The last start of {record.Version ?? "the new version"} never showed its window; going back to the previous version.");
            return RollBack(currentExe, record.CompanionSwapped) ? ProbationStartup.RolledBack : ProbationStartup.None;
        }

        record.LaunchPid = processId;
        record.LaunchStartedUtc = processStartUtc;
        Save(record);
        return ProbationStartup.Watching;
    }

    /// <summary>
    /// Called once the main window has shown. Returns true when this launch ended the probation and
    /// the previous build was removed.
    /// </summary>
    public bool MarkLoaded(string currentExe, int processId)
    {
        var record = Read();
        if (record is null || !SamePath(record.TargetExe, currentExe) || record.LaunchPid != processId)
        {
            return false;
        }

        record.LoadedLaunches++;
        record.LaunchPid = null;
        record.LaunchStartedUtc = null;

        if (record.LoadedLaunches < LaunchesToCommit)
        {
            Save(record);
            _log.Write(LogLevel.Info,
                $"Update to {record.Version} loaded ({record.LoadedLaunches} of {LaunchesToCommit}); the previous build is kept for now.");
            return false;
        }

        TryDelete(BackupPath(currentExe));
        if (record.CompanionSwapped)
        {
            TryDelete(BackupPath(CompanionPath(currentExe)));
        }

        Clear();
        _log.Write(LogLevel.Info, $"Update to {record.Version} loaded {LaunchesToCommit} times; the previous build was removed.");
        return true;
    }

    /// <summary>Removes the build a rollback set aside; it could not be deleted while it was running.</summary>
    public static void RemoveFailedBuild(string currentExe)
    {
        TryDelete(FailedPath(currentExe));
    }

    private bool RollBack(string target, bool companionSwapped)
    {
        var backup = BackupPath(target);
        var failed = FailedPath(target);
        try
        {
            TryDelete(failed);

            // A running exe can be renamed but not deleted, so the failed build steps aside.
            File.Move(target, failed);
            try
            {
                File.Move(backup, target);
            }
            catch
            {
                File.Move(failed, target);
                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Write(LogLevel.Error,
                $"Could not go back to the previous version; it is kept as {Path.GetFileName(backup)} next to the app: {LogPrivacy.Describe(ex)}");
            return false;
        }

        var companion = CompanionPath(target);
        var companionBackup = BackupPath(companion);
        if (companionSwapped && File.Exists(companionBackup))
        {
            try
            {
                File.Move(companionBackup, companion, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Write(LogLevel.Warning,
                    $"The previous command line could not be put back; it is kept as {Path.GetFileName(companionBackup)} next to the app: {LogPrivacy.Describe(ex)}");
            }
        }

        Clear();
        _log.Write(LogLevel.Warning, "The previous version was restored.");
        return true;
    }

    private void Save(UpdateProbationRecord record) =>
        FileUtil.AtomicWriteAllText(_path, JsonSerializer.Serialize(record, JsonDefaults.Indented));

    private static bool SamePath(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static bool IsRunning(int processId, DateTime? startedUtc)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return false;
            }

            // A reused id belongs to some other process that started later.
            return startedUtc is not { } expected ||
                   Math.Abs((process.StartTime.ToUniversalTime() - expected).TotalSeconds) < 2;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // Cannot tell: assume it is running, so nothing is rolled back on a guess.
            return true;
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
            // Best effort; a locked file is retried on a later start.
        }
    }
}
