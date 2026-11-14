using System.Text;
using System.Text.Json;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Models;
using ICSharpCode.SharpZipLib.Zip;

namespace CtrlZ.Core.Services;

/// <summary>
/// Creates one portable ZIP per browser. Archives contain a manifest, profile files, a
/// sanitized Local State, and a sanitized Web Data (search engines only). They never contain
/// Login Data/Cookies, and cache directories are skipped by the planner.
/// </summary>
public sealed class BrowserBackupService
{
    private readonly AppPaths _paths;
    private readonly IAppLog _log;
    private readonly BrowserProcessService _processes;

    public BrowserBackupService(AppPaths paths, IAppLog log, BrowserProcessService processes)
    {
        _paths = paths;
        _log = log;
        _processes = processes;
    }

    public BrowserBackupEstimate Estimate(DetectedBrowser browser, IReadOnlyList<BrowserProfile> profiles)
    {
        var plan = BrowserBackupPlanner.Build(browser, profiles);
        return BrowserBackupPlanner.Estimate(plan);
    }

    public async Task<BrowserBackupResult> BackupAsync(
        BrowserBackupRequest request,
        IProgress<BrowserBackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (request.Profiles.Count == 0)
        {
            throw new BrowserBackupException("Select at least one browser profile.");
        }

        if (request.Password is { Length: > 0 and < 4 })
        {
            throw new BrowserBackupException("Use at least 4 characters for the archive password.");
        }

        if (!string.IsNullOrWhiteSpace(request.Browser.UserDataPath))
        {
            Directory.CreateDirectory(request.Browser.UserDataPath);
        }

        if (_processes.IsRunning(request.Browser))
        {
            throw new BrowserRunningException(
                request.Browser.DisplayName,
                _processes.FindRunning(request.Browser).Select(p => p.Name).Distinct().ToArray());
        }

        var destinationFolder = Path.GetFullPath(request.DestinationFolder);
        if (!string.IsNullOrWhiteSpace(request.Browser.UserDataPath) &&
            FileUtil.IsUnderDirectory(destinationFolder, request.Browser.UserDataPath))
        {
            throw new BrowserBackupException("Choose a backup folder outside the browser's profile folder.");
        }

        Directory.CreateDirectory(destinationFolder);
        return await Task.Run(
            () => BackupCore(request, destinationFolder, progress, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private BrowserBackupResult BackupCore(
        BrowserBackupRequest request,
        string destinationFolder,
        IProgress<BrowserBackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var plan = BrowserBackupPlanner.Build(request.Browser, request.Profiles);
        var warnings = new List<string>(plan.Warnings);
        var total = plan.FileCount;
        progress?.Report(new BrowserBackupProgress("Preparing archive…", 0, total, 0));

        var timestamp = DateTime.Now;
        var archiveName = $"CTRLZ-{FileUtil.SafeFileName(request.Browser.DisplayName)}-{timestamp:yyyyMMdd-HHmm}.zip";
        var archivePath = Path.Combine(destinationFolder, archiveName);
        var unique = 1;
        while (File.Exists(archivePath))
        {
            archivePath = Path.Combine(destinationFolder,
                $"CTRLZ-{FileUtil.SafeFileName(request.Browser.DisplayName)}-{timestamp:yyyyMMdd-HHmm}-{unique++}.zip");
        }

        var tempArchive = archivePath + ".partial";
        var workDirectory = Path.Combine(_paths.Cache, "browser-backup", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(workDirectory);
        VolumeShadowCopySession? snapshot = null;
        try
        {
            var webDataFiles = PrepareWebData(plan, workDirectory, warnings, ref snapshot, cancellationToken);
            var manifest = BuildManifest(request, plan, warnings);
            var processed = 0;
            long writtenBytes = 0;
            var writtenEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Streaming to a temp archive avoids exposing a half-written backup.
            using (var fileStream = new FileStream(tempArchive, FileMode.Create, FileAccess.Write, FileShare.None,
                       81920, FileOptions.SequentialScan))
            using (var zip = new ZipOutputStream(fileStream))
            {
                zip.SetLevel(6);
                zip.UseZip64 = UseZip64.Dynamic;
                var password = string.IsNullOrWhiteSpace(request.Password) ? null : request.Password;
                if (password is not null)
                {
                    zip.Password = password;
                }

                foreach (var entry in plan.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    byte[]? bytes = null;
                    string? tempFile = null;
                    try
                    {
                        if (entry.Kind == BrowserPlanEntryKind.LocalState)
                        {
                            var sourceBytes = ReadAllBytesWithFallback(entry.SourcePath, ref snapshot);
                            var sanitizedState = BrowserLocalStateSanitizer.Sanitize(Encoding.UTF8.GetString(sourceBytes));
                            bytes = JsonSerializer.SerializeToUtf8Bytes(sanitizedState,
                                new JsonSerializerOptions { WriteIndented = false });
                        }
                        else if (entry.Kind == BrowserPlanEntryKind.WebData)
                        {
                            if (!webDataFiles.TryGetValue(entry.EntryPath, out tempFile) || !File.Exists(tempFile))
                            {
                                warnings.Add($"Skipped Web Data for {entry.EntryPath}; search engines could not be sanitized.");
                                continue;
                            }
                        }
                        else
                        {
                            tempFile = null;
                        }

                        if (bytes is null && entry.Kind == BrowserPlanEntryKind.CopyFile)
                        {
                            using var source = OpenWithFallback(entry.SourcePath, ref snapshot);
                            WriteZipEntry(zip, entry.EntryPath, source, password is not null, progress, processed + 1,
                                total);
                            writtenBytes += source.Length;
                        }
                        else if (bytes is not null)
                        {
                            using var stream = new MemoryStream(bytes);
                            WriteZipEntry(zip, entry.EntryPath, stream, password is not null, progress, processed + 1,
                                total);
                            writtenBytes += stream.Length;
                        }
                        else if (tempFile is not null)
                        {
                            using var stream = new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                            WriteZipEntry(zip, entry.EntryPath, stream, password is not null, progress, processed + 1,
                                total);
                            writtenBytes += stream.Length;
                        }

                        writtenEntries.Add(entry.EntryPath);
                        processed++;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        throw new BrowserBackupLockedException(
                            $"'{entry.EntryPath}' is locked. Close {request.Browser.DisplayName} (force close if needed) and try again. " +
                            "Volume Shadow Copy is used automatically when available.", ex);
                    }
                    finally
                    {
                        if (entry.Kind == BrowserPlanEntryKind.WebData && tempFile is not null)
                        {
                            TryDelete(tempFile);
                        }
                    }
                }

                manifest.FileCount = processed;
                manifest.TotalBytes = writtenBytes;
                manifest.Warnings = warnings;
                manifest.Contents = BuildContents(plan, writtenEntries);
                if (password is not null)
                {
                    manifest.PasswordProtected = true;
                }

                var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonDefaults.Profile);
                using (var manifestStream = new MemoryStream(manifestBytes))
                {
                    WriteZipEntry(zip, "manifest.json", manifestStream, password is not null, progress, total, total);
                }
            }

            File.Move(tempArchive, archivePath, overwrite: false);
            var archiveBytes = new FileInfo(archivePath).Length;
            progress?.Report(new BrowserBackupProgress("Backup complete.", total, total, 1));
            _log.Write(LogLevel.Info,
                $"Browser backup written to {archivePath} ({FileUtil.FormatBytes(archiveBytes)}, {processed} files).");

            return new BrowserBackupResult(archivePath, manifest, archiveBytes);
        }
        catch
        {
            TryDelete(tempArchive);
            throw;
        }
        finally
        {
            snapshot?.Dispose();
            TryDeleteDirectory(workDirectory);
        }
    }

    private Dictionary<string, string> PrepareWebData(
        BrowserBackupPlan plan,
        string workDirectory,
        List<string> warnings,
        ref VolumeShadowCopySession? snapshot,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in plan.Entries.Where(e => e.Kind == BrowserPlanEntryKind.WebData))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var copyPath = Path.Combine(workDirectory, Guid.NewGuid().ToString("n") + ".db");
            var sanitizedPath = Path.Combine(workDirectory, Guid.NewGuid().ToString("n") + "-sanitized.db");
            try
            {
                using (var source = OpenWithFallback(entry.SourcePath, ref snapshot))
                using (var destination = new FileStream(copyPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    source.CopyTo(destination);
                }

                WebDataSanitizer.Sanitize(copyPath, sanitizedPath, _log);
                result[entry.EntryPath] = sanitizedPath;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"Web Data skipped for {entry.EntryPath}: {ex.Message}");
                _log.Write(LogLevel.Warning, $"Web Data sanitization skipped for {entry.SourcePath}.", ex);
            }
            finally
            {
                TryDelete(copyPath);
            }
        }

        return result;
    }

    private static void WriteZipEntry(
        ZipOutputStream zip,
        string entryPath,
        Stream source,
        bool encrypted,
        IProgress<BrowserBackupProgress>? progress,
        int processed,
        int total)
    {
        var entry = new ZipEntry(entryPath.Replace('\\', '/'))
        {
            DateTime = DateTime.Now,
            Size = source.CanSeek ? source.Length : -1
        };
        if (encrypted)
        {
            entry.AESKeySize = 256;
        }

        zip.PutNextEntry(entry);
        source.CopyTo(zip, 81920);
        zip.CloseEntry();
        progress?.Report(new BrowserBackupProgress(
            $"Compressing {Path.GetFileName(entryPath)}…", processed, total,
            total == 0 ? 0 : (double)processed / total));
    }

    private Stream OpenWithFallback(string path, ref VolumeShadowCopySession? snapshot)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            snapshot ??= CreateSnapshot(path);
            var shadowPath = snapshot.MapPath(path);
            return new FileStream(shadowPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
    }

    private byte[] ReadAllBytesWithFallback(string path, ref VolumeShadowCopySession? snapshot)
    {
        using var stream = OpenWithFallback(path, ref snapshot);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private VolumeShadowCopySession CreateSnapshot(string path)
    {
        if (!VolumeShadowCopySession.TryCreate(path, _log, out var snapshot, out var error))
        {
            throw new BrowserBackupLockedException(
                "A browser file is locked and Windows Volume Shadow Copy is unavailable. " +
                "Close the browser (or restart CTRL+Z as administrator) and try again. " + error);
        }

        return snapshot!;
    }

    private static BrowserBackupManifest BuildManifest(
        BrowserBackupRequest request,
        BrowserBackupPlan plan,
        List<string> warnings)
    {
        return new BrowserBackupManifest
        {
            AppVersion = typeof(BrowserBackupService).Assembly.GetName().Version?.ToString(3) ?? "1.1.0",
            Family = request.Browser.Family,
            BrowserId = request.Browser.Id,
            BrowserName = request.Browser.DisplayName,
            BrowserVersion = request.Browser.Version,
            CreatedUtc = DateTimeOffset.UtcNow,
            FlatProfileLayout = request.Profiles.Any(p => p.IsFlat),
            Profiles = request.Profiles.Select(p => new BrowserBackupProfileManifest
            {
                DirectoryName = p.DirectoryName,
                DisplayName = p.DisplayName,
                UserName = p.UserName,
                IsDefault = p.IsDefault
            }).ToList(),
            Contents = BuildContents(plan, plan.Entries.Select(entry => entry.EntryPath).ToHashSet(StringComparer.OrdinalIgnoreCase)),
            Warnings = warnings
        };
    }

    private static BrowserBackupContents BuildContents(BrowserBackupPlan plan, IReadOnlySet<string> written)
    {
        if (plan.Browser.Family == BrowserFamily.Firefox)
        {
            return new BrowserBackupContents
            {
                Preferences = written.Any(e => e.EndsWith("/prefs.js", StringComparison.OrdinalIgnoreCase) ||
                                               e.EndsWith("/user.js", StringComparison.OrdinalIgnoreCase)),
                Bookmarks = written.Any(e => e.EndsWith("/places.sqlite", StringComparison.OrdinalIgnoreCase)),
                History = written.Any(e => e.EndsWith("/places.sqlite", StringComparison.OrdinalIgnoreCase)),
                Sessions = written.Any(e => e.Contains("sessionstore", StringComparison.OrdinalIgnoreCase)),
                Extensions = written.Any(e => e.Contains("/extensions/", StringComparison.OrdinalIgnoreCase) ||
                                              e.EndsWith("/extensions.json", StringComparison.OrdinalIgnoreCase) ||
                                              e.Contains("/moz-extension", StringComparison.OrdinalIgnoreCase)),
                SearchEngines = written.Any(e => e.Contains("search.json", StringComparison.OrdinalIgnoreCase) ||
                                                 e.EndsWith("/search.sqlite", StringComparison.OrdinalIgnoreCase)),
                SitePermissions = written.Any(e => e.EndsWith("/permissions.sqlite", StringComparison.OrdinalIgnoreCase)),
                Excluded = FirefoxDataRules.ExcludedDirectoryNames
                    .Concat(FirefoxDataRules.ForbiddenFileNames)
                    .Concat(["saved passwords", "cookies", "form history"])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase)
                    .ToList()
            };
        }

        return new BrowserBackupContents
        {
            LocalState = written.Contains("User Data/Local State"),
            Preferences = written.Any(e => e.EndsWith("/Preferences", StringComparison.OrdinalIgnoreCase)),
            Bookmarks = written.Any(e => e.EndsWith("/Bookmarks", StringComparison.OrdinalIgnoreCase)),
            History = written.Any(e => e.EndsWith("/History", StringComparison.OrdinalIgnoreCase)),
            Sessions = written.Any(e => e.Contains("/Sessions/", StringComparison.OrdinalIgnoreCase) ||
                                        e.EndsWith("/Current Session", StringComparison.OrdinalIgnoreCase) ||
                                        e.EndsWith("/Last Session", StringComparison.OrdinalIgnoreCase)),
            Extensions = written.Any(e => e.Contains("/Extensions/", StringComparison.OrdinalIgnoreCase)),
            SearchEngines = written.Any(e => e.EndsWith("/Web Data", StringComparison.OrdinalIgnoreCase)),
            SitePermissions = written.Any(e => e.EndsWith("/Preferences", StringComparison.OrdinalIgnoreCase)),
            Excluded = BrowserDataRules.ExcludedDirectoryNames
                .Concat(BrowserDataRules.ForbiddenFileNames)
                .Concat(["saved passwords", "cookies"])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase)
                .ToList()
        };
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch { }
    }
}
