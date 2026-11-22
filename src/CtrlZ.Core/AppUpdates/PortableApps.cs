// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Text.Json;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.AppUpdates;

/// <summary>Creates Start menu shortcuts. An interface so tests never touch the real Start menu.</summary>
public interface IShortcutWriter
{
    /// <summary>Creates or replaces a shortcut; returns its path, or null when it could not.</summary>
    string? Create(string name, string targetExe);
}

/// <summary>
/// Shortcuts for portable apps in the user's own Start menu, under "CTRL+Z apps". Uses the
/// Windows Script Host shell object; no admin rights and nothing outside the user's profile.
/// </summary>
public sealed class StartMenuShortcutWriter : IShortcutWriter
{
    public const string FolderName = "CTRL+Z apps";

    private readonly IAppLog _log;
    private readonly string _folder;

    public StartMenuShortcutWriter(IAppLog log, string? programsFolder = null)
    {
        _log = log;
        _folder = Path.Combine(
            programsFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            FolderName);
    }

    public string? Create(string name, string targetExe)
    {
        try
        {
            if (!File.Exists(targetExe))
            {
                return null;
            }

            Directory.CreateDirectory(_folder);
            var path = Path.Combine(_folder, FileUtil.Slug(name) + ".lnk");
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return null;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                dynamic link = shell.CreateShortcut(path);
                link.TargetPath = targetExe;
                link.WorkingDirectory = Path.GetDirectoryName(targetExe);
                link.Description = name;
                link.Save();
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }

            return path;
        }
        catch (Exception ex)
        {
            _log.Write(LogLevel.Warning, $"The Start menu shortcut for {name} could not be created.", ex);
            return null;
        }
    }
}

/// <summary>Which GitHub release each app was installed from, so newer releases can be offered.</summary>
public sealed class GitHubReleaseTracker
{
    public const string FileName = "github-releases.json";

    private readonly string _path;
    private Dictionary<string, TrackedRelease> _apps = new(StringComparer.Ordinal);

    public GitHubReleaseTracker(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(_path) &&
                JsonSerializer.Deserialize<Dictionary<string, TrackedRelease>>(File.ReadAllText(_path), JsonDefaults.Profile) is { } loaded)
            {
                _apps = new Dictionary<string, TrackedRelease>(
                    loaded.Where(pair => pair.Value is not null && !string.IsNullOrWhiteSpace(pair.Value.Tag)),
                    StringComparer.Ordinal);
            }
        }
        catch (Exception)
        {
            _apps = new Dictionary<string, TrackedRelease>(StringComparer.Ordinal);
        }
    }

    public TrackedRelease? Get(string appId) => _apps.GetValueOrDefault(appId);

    public void Record(string appId, string repository, string tag)
    {
        _apps[appId] = new TrackedRelease(repository, tag, DateTimeOffset.UtcNow);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_apps, JsonDefaults.Profile));
        }
        catch (Exception)
        {
            // Best effort: a lost record only means no update offer for that app.
        }
    }
}

public sealed record TrackedRelease(string Repository, string Tag, DateTimeOffset InstalledUtc);

/// <summary>An app installed from a GitHub release that has a newer release.</summary>
public sealed record GitHubAppUpdate(AppEntry App, string InstalledTag, string LatestTag);

public static class GitHubUpdateChecker
{
    /// <summary>
    /// Compares each tracked GitHub app with its repository's latest release. An app installed
    /// before tracking existed, or whose repository changed, is not offered (nothing to compare).
    /// </summary>
    public static async Task<IReadOnlyList<GitHubAppUpdate>> CheckAsync(
        IEnumerable<AppEntry> apps,
        GitHubReleaseTracker tracker,
        Func<string, CancellationToken, Task<string>> latestTag,
        CancellationToken cancellationToken = default)
    {
        var updates = new List<GitHubAppUpdate>();
        foreach (var app in apps.Where(app => app.Source == AppSource.GitHub && !string.IsNullOrWhiteSpace(app.GitHubRepo)))
        {
            var tracked = tracker.Get(app.Id);
            if (tracked is null || !string.Equals(tracked.Repository, app.GitHubRepo, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string latest;
            try
            {
                latest = await latestTag(app.GitHubRepo!, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(latest) && !SameTag(tracked.Tag, latest))
            {
                updates.Add(new GitHubAppUpdate(app, tracked.Tag, latest));
            }
        }

        return updates;
    }

    /// <summary>"v1.2.0" and "1.2.0" are the same release.</summary>
    public static bool SameTag(string a, string b) =>
        string.Equals(a.Trim().TrimStart('v', 'V'), b.Trim().TrimStart('v', 'V'), StringComparison.OrdinalIgnoreCase);
}
