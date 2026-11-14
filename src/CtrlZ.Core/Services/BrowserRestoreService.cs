using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Models;
using ICSharpCode.SharpZipLib.Zip;

namespace CtrlZ.Core.Services;

/// <summary>
/// Restores selected profiles from a CTRL+Z browser archive. Existing cookies/passwords are
/// never deleted (the archive does not contain them), and a restore can create new profile
/// folders instead of overwriting the user's current ones.
/// </summary>
public sealed class BrowserRestoreService
{
    private readonly AppPaths _paths;
    private readonly IAppLog _log;
    private readonly BrowserProcessService _processes;
    private readonly BrowserArchiveReader _reader;

    public BrowserRestoreService(
        AppPaths paths,
        IAppLog log,
        BrowserProcessService processes,
        BrowserArchiveReader reader)
    {
        _paths = paths;
        _log = log;
        _processes = processes;
        _reader = reader;
    }

    public Task<BrowserArchiveInfo> InspectAsync(
        string archivePath,
        string? password,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => _reader.ReadInfo(archivePath, password), cancellationToken);
    }

    public async Task<BrowserRestoreResult> RestoreAsync(
        BrowserRestoreRequest request,
        IProgress<BrowserRestoreProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Target.UserDataPath))
        {
            throw new BrowserBackupException("Choose the target browser's User Data folder first.");
        }

        if (!ChromiumProfileReader.ValidateUserDataFolder(request.Target.UserDataPath, out var pathError))
        {
            throw new BrowserBackupException(pathError);
        }

        if (_processes.IsRunning(request.Target))
        {
            throw new BrowserRunningException(
                request.Target.DisplayName,
                _processes.FindRunning(request.Target).Select(p => p.Name).Distinct().ToArray());
        }

        return await Task.Run(
            () => RestoreCore(request, progress, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private BrowserRestoreResult RestoreCore(
        BrowserRestoreRequest request,
        IProgress<BrowserRestoreProgress>? progress,
        CancellationToken cancellationToken)
    {
        var userData = Path.GetFullPath(request.Target.UserDataPath!);
        var info = _reader.ReadInfo(request.ArchivePath, request.Password);
        var manifest = info.Manifest;
        var selected = manifest.Profiles
            .Where(p => request.ProfileDirectoryNames.Contains(p.DirectoryName, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (selected.Count == 0)
        {
            throw new BrowserBackupException("Select at least one profile from the archive.");
        }

        var warnings = new List<string>();
        var targetFlat = request.Target.Profiles.Any(p => p.IsFlat) || manifest.FlatProfileLayout;
        var mappings = BuildMappings(request, selected, userData, targetFlat, warnings);
        var conflicts = mappings
            .Where(pair => !pair.Value.Equals(".", StringComparison.Ordinal))
            .Select(pair => pair.Value)
            .Where(targetDir => Directory.Exists(Path.Combine(userData, targetDir)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (!request.RestoreAsNewProfiles && conflicts.Length > 0)
        {
            throw new BrowserRestoreConflictException(conflicts);
        }

        if (!string.Equals(manifest.BrowserId, request.Target.Id, StringComparison.OrdinalIgnoreCase) &&
            !request.Target.IsCustom)
        {
            warnings.Add($"This archive was created from {manifest.BrowserName}, not {request.Target.DisplayName}.");
        }

        var safetyFolder = CreateSafetyBackup(userData, mappings, selected, warnings);
        var archivedLocalState = ReadArchivedLocalState(info.ArchivePath, request.Password, cancellationToken);
        var processed = 0;
        var total = CountFileEntries(info.ArchivePath, request.Password);
        progress?.Report(new BrowserRestoreProgress("Preparing restore…", 0, total, 0));

        using var zip = OpenArchive(request.ArchivePath, request.Password);
        for (var i = 0; i < zip.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = zip[i];
            if (entry is null || entry.IsDirectory)
            {
                continue;
            }

            var entryName = entry.Name.Replace('\\', '/');
            if (entryName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!entryName.StartsWith("User Data/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = entryName["User Data/".Length..];
            if (relative.Equals("Local State", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var targetRelative = ResolveTargetRelative(relative, mappings, selected, targetFlat);
            if (targetRelative is null)
            {
                continue;
            }

            if (BrowserArchiveReader.IsForbiddenRestorePath(targetRelative))
            {
                warnings.Add($"Skipped sensitive file from archive: {targetRelative}");
                continue;
            }

            var targetPath = ResolveSafeTarget(userData, targetRelative);
            if (targetPath is null)
            {
                warnings.Add($"Skipped unsafe archive path: {entryName}");
                continue;
            }

            CopyForSafety(targetPath, userData, safetyFolder);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            using (var source = zip.GetInputStream(entry))
            using (var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(target, 81920);
            }

            processed++;
            progress?.Report(new BrowserRestoreProgress(
                $"Restoring {Path.GetFileName(targetRelative)}…", processed, total,
                total == 0 ? 0 : (double)processed / total));
        }

        RestoreLocalState(userData, archivedLocalState, mappings, manifest, selected, targetFlat, warnings);
        progress?.Report(new BrowserRestoreProgress("Restore complete.", total, total, 1));
        _log.Write(LogLevel.Info,
            $"Restored {processed} files into {userData} for {request.Target.DisplayName}.");

        return new BrowserRestoreResult(
            request.Target.DisplayName,
            userData,
            mappings.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            warnings,
            safetyFolder);
    }

    private static Dictionary<string, string> BuildMappings(
        BrowserRestoreRequest request,
        IReadOnlyList<BrowserBackupProfileManifest> selected,
        string userData,
        bool targetFlat,
        List<string> warnings)
    {
        var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in selected)
        {
            if (targetFlat)
            {
                if (request.RestoreAsNewProfiles)
                {
                    warnings.Add("This browser uses a single-profile layout, so it cannot create an additional profile. The existing profile will be overwritten.");
                }

                mappings[profile.DirectoryName] = ".";
                continue;
            }

            if (!IsSafeProfileDirectoryName(profile.DirectoryName))
            {
                warnings.Add($"Skipped profile with an unsafe folder name: {profile.DirectoryName}");
                continue;
            }

            if (!request.RestoreAsNewProfiles)
            {
                mappings[profile.DirectoryName] = profile.DirectoryName;
                used.Add(profile.DirectoryName);
                continue;
            }

            var target = NextProfileName(userData, used);
            used.Add(target);
            mappings[profile.DirectoryName] = target;
        }

        return mappings;
    }

    private static string NextProfileName(string userData, HashSet<string> reserved)
    {
        var number = 1;
        while (true)
        {
            var candidate = $"Profile {number}";
            if (!Directory.Exists(Path.Combine(userData, candidate)) && reserved.Add(candidate))
            {
                return candidate;
            }

            number++;
        }
    }

    private static string? ResolveTargetRelative(
        string relative,
        IReadOnlyDictionary<string, string> mappings,
        IReadOnlyList<BrowserBackupProfileManifest> selected,
        bool targetFlat)
    {
        var normalized = relative.Replace('\\', '/');
        if (targetFlat)
        {
            var firstSlash = normalized.IndexOf('/');
            if (firstSlash > 0)
            {
                var possibleSource = normalized[..firstSlash];
                if (selected.Any(p => p.DirectoryName.Equals(possibleSource, StringComparison.OrdinalIgnoreCase)))
                {
                    normalized = normalized[(firstSlash + 1)..];
                }
            }

            return normalized;
        }

        var slash = normalized.IndexOf('/');
        if (slash <= 0)
        {
            return null;
        }

        var sourceProfile = normalized[..slash];
        if (!mappings.TryGetValue(sourceProfile, out var targetProfile))
        {
            return null;
        }

        return Path.Combine(targetProfile, normalized[(slash + 1)..]);
    }

    private static string? ResolveSafeTarget(string userData, string relative)
    {
        try
        {
            var combined = Path.GetFullPath(Path.Combine(userData, relative));
            if (combined.Equals(userData, StringComparison.OrdinalIgnoreCase) ||
                !FileUtil.IsUnderDirectory(combined, userData))
            {
                return null;
            }

            return combined;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSafeProfileDirectoryName(string directoryName)
    {
        return !string.IsNullOrWhiteSpace(directoryName) &&
               directoryName is not ("." or "..") &&
               directoryName.IndexOfAny(['/', '\\']) < 0 &&
               directoryName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    private string? ReadArchivedLocalState(string archivePath, string? password, CancellationToken cancellationToken)
    {
        using var zip = OpenArchive(archivePath, password);
        var entry = zip.GetEntry("User Data/Local State");
        if (entry is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (entry.Size > 64 * 1024 * 1024)
        {
            throw new InvalidDataException("Local State in the archive is unexpectedly large.");
        }

        using var stream = zip.GetInputStream(entry);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private static void RestoreLocalState(
        string userData,
        string? archivedLocalState,
        IReadOnlyDictionary<string, string> mappings,
        BrowserBackupManifest manifest,
        IReadOnlyList<BrowserBackupProfileManifest> selected,
        bool targetFlat,
        List<string> warnings)
    {
        var targetPath = Path.Combine(userData, "Local State");
        string? current = null;
        try
        {
            if (File.Exists(targetPath))
            {
                current = File.ReadAllText(targetPath);
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"The current Local State could not be read: {ex.Message}");
        }

        JsonObject merged;
        try
        {
            merged = BrowserLocalStateSanitizer.MergeForRestore(
                archivedLocalState,
                current,
                mappings,
                mergeProfileEntries: !targetFlat);
        }
        catch (Exception ex)
        {
            warnings.Add($"Local State could not be merged: {ex.Message}");
            return;
        }

        if (!targetFlat)
        {
            var profileNode = merged["profile"] as JsonObject ?? new JsonObject();
            var infoCache = profileNode["info_cache"] as JsonObject ?? new JsonObject();
            var existingNames = infoCache.Select(pair =>
                    (pair.Value as JsonObject)?["name"]?.GetValue<string>() ?? string.Empty)
                .Where(name => name.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var source in selected)
            {
                if (!mappings.TryGetValue(source.DirectoryName, out var targetDir) || targetDir == ".")
                {
                    continue;
                }

                if (infoCache[targetDir] is not JsonObject targetEntry)
                {
                    targetEntry = new JsonObject();
                    infoCache[targetDir] = targetEntry;
                }

                var displayName = string.IsNullOrWhiteSpace(source.DisplayName)
                    ? targetDir
                    : source.DisplayName;
                var isNewFolder = !targetDir.Equals(source.DirectoryName, StringComparison.OrdinalIgnoreCase);
                if (isNewFolder)
                {
                    var candidate = $"{displayName} (restored)";
                    var suffix = 2;
                    while (existingNames.Contains(candidate))
                    {
                        candidate = $"{displayName} (restored {suffix++})";
                    }

                    targetEntry["name"] = candidate;
                    existingNames.Add(candidate);
                }
                else if (targetEntry["name"] is null)
                {
                    var candidate = existingNames.Contains(displayName) ? $"{displayName} (restored)" : displayName;
                    targetEntry["name"] = candidate;
                    existingNames.Add(candidate);
                }

                if (source.UserName is not null && targetEntry["user_name"] is null)
                {
                    targetEntry["user_name"] = source.UserName;
                }
            }

            profileNode["info_cache"] = infoCache;
            merged["profile"] = profileNode;
        }

        try
        {
            FileUtil.AtomicWriteAllText(targetPath,
                JsonSerializer.Serialize(merged, new JsonSerializerOptions { WriteIndented = false }));
        }
        catch (Exception ex)
        {
            throw new BrowserBackupException($"Profile files were restored, but Local State could not be written: {ex.Message}", ex);
        }
    }

    private string? CreateSafetyBackup(
        string userData,
        IReadOnlyDictionary<string, string> mappings,
        IReadOnlyList<BrowserBackupProfileManifest> selected,
        List<string> warnings)
    {
        try
        {
            var root = Path.Combine(
                _paths.Root, "restore-backups", $"restore-{DateTime.Now:yyyyMMdd-HHmmss}");
            var localState = Path.Combine(userData, "Local State");
            if (File.Exists(localState))
            {
                Directory.CreateDirectory(root);
                File.Copy(localState, Path.Combine(root, "Local State"), overwrite: true);
            }

            foreach (var profile in selected)
            {
                if (!mappings.TryGetValue(profile.DirectoryName, out var targetDir) || targetDir == ".")
                {
                    continue;
                }

                var directory = Path.Combine(userData, targetDir);
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                var destination = Path.Combine(root, targetDir);
                foreach (var fileName in new[] { "Preferences", "Secure Preferences", "Bookmarks", "History" })
                {
                    var source = Path.Combine(directory, fileName);
                    if (File.Exists(source))
                    {
                        Directory.CreateDirectory(destination);
                        File.Copy(source, Path.Combine(destination, fileName), overwrite: true);
                    }
                }
            }

            return Directory.Exists(root) ? root : null;
        }
        catch (Exception ex)
        {
            warnings.Add($"A safety copy of the current profile could not be created: {ex.Message}");
            return null;
        }
    }

    private static void CopyForSafety(string path, string userData, string? safetyFolder)
    {
        if (safetyFolder is null || !File.Exists(path))
        {
            return;
        }

        var name = Path.GetFileName(path);
        if (name is not ("Preferences" or "Secure Preferences" or "Bookmarks" or "History" or "Local State"))
        {
            return;
        }

        try
        {
            var relative = Path.GetRelativePath(userData, path);
            var destination = Path.Combine(safetyFolder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(path, destination, overwrite: true);
        }
        catch
        {
            // Safety copies are best effort; the pre-restore warning remains authoritative.
        }
    }

    private static int CountFileEntries(string archivePath, string? password)
    {
        using var zip = OpenArchive(archivePath, password);
        var count = 0;
        for (var i = 0; i < zip.Count; i++)
        {
            var entry = zip[i];
            if (entry is not null && !entry.IsDirectory &&
                entry.Name.StartsWith("User Data/", StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }

        return count;
    }

    private static ZipFile OpenArchive(string archivePath, string? password)
    {
        var zip = new ZipFile(archivePath);
        if (!string.IsNullOrWhiteSpace(password))
        {
            zip.Password = password;
        }

        return zip;
    }
}
