// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Logging;

/// <summary>What the diagnostics summary is built from. Profiles are only ever counted.</summary>
public sealed record DiagnosticsInput
{
    public required string AppVersion { get; init; }
    public string OsVersion { get; init; } = Environment.OSVersion.VersionString;
    public string OsArchitecture { get; init; } = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString();
    public string ProcessArchitecture { get; init; } = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
    public string Runtime { get; init; } = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;

    /// <summary>"installed", "portable" or "custom folder": never the folder itself.</summary>
    public required string DataLocation { get; init; }

    public string? WingetStatus { get; init; }
    public required AppSettings Settings { get; init; }
    public int ProfileCount { get; init; }

    /// <summary>The profile already open in the app. Its apps are counted by kind, nothing more.</summary>
    public AppProfile? ActiveProfile { get; init; }
    public InstallState? LastInstall { get; init; }
    public DateTimeOffset CreatedLocal { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// A diagnostics ZIP made only when the user asks for one. It holds a summary of counts, versions
/// and switches, and recent logs with paths removed. It never holds profiles, app notes, post-install
/// commands, saved folders or the account and PC names, and nothing is sent anywhere: the user
/// decides whether to share the file.
/// </summary>
public static partial class DiagnosticsBundle
{
    public const string SummaryEntry = "summary.txt";
    public const string ReadMeEntry = "README.txt";
    public const int MaxLogFiles = 7;
    public const int MaxLinesPerLog = 5000;

    /// <summary>Versions, switches and counts. No names, paths or free text from the user.</summary>
    public static string Summary(DiagnosticsInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var settings = input.Settings;
        List<AppEntry> apps = input.ActiveProfile?.Apps ?? [];
        var text = new StringBuilder()
            .AppendLine($"CTRL+Z {input.AppVersion}")
            .AppendLine($"Created: {input.CreatedLocal:yyyy-MM-dd HH:mm zzz}")
            .AppendLine($"Windows: {input.OsVersion} ({input.OsArchitecture})")
            .AppendLine($"Process: {input.ProcessArchitecture}, {input.Runtime}")
            .AppendLine($"Data: {input.DataLocation}")
            .AppendLine($"winget: {Clean(input.WingetStatus ?? "not checked")}")
            .AppendLine()
            .AppendLine("Settings")
            .AppendLine($"  Theme: {settings.Theme}")
            .AppendLine($"  Update channel: {settings.UpdateChannel}")
            .AppendLine($"  Check for updates automatically: {YesNo(settings.AutoCheckForUpdates)}")
            .AppendLine($"  Show install preview: {YesNo(settings.ShowInstallPreview)}")
            .AppendLine($"  Start menu shortcuts for portable apps: {YesNo(settings.CreatePortableShortcuts)}")
            .AppendLine($"  Include notes in reports: {YesNo(settings.IncludeNotesInReports)}")
            .AppendLine($"  Refresh catalog automatically: {YesNo(settings.AutoRefreshCatalog)}")
            .AppendLine($"  Custom browser data folder: {YesNo(!string.IsNullOrWhiteSpace(settings.CustomBrowserUserDataPath))}")
            .AppendLine()
            .AppendLine("Profiles")
            .AppendLine($"  Saved profiles: {input.ProfileCount}")
            .AppendLine($"  Apps in the open profile: {apps.Count}");

        foreach (var group in apps.GroupBy(app => app.Source).OrderBy(group => group.Key))
        {
            text.AppendLine($"    {group.Key}: {group.Count()}");
        }

        text.AppendLine($"  Added as dependencies: {apps.Count(app => app.IsAutoDependency)}")
            .AppendLine($"  With a post-install command: {apps.Count(app => app.PostInstall is not null)}")
            .AppendLine($"  With a note: {apps.Count(app => app.HasNotes)}")
            .AppendLine()
            .AppendLine("Last install run");

        if (input.LastInstall is { } run)
        {
            text.AppendLine($"  Started: {run.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm}")
                .AppendLine($"  Finished: {YesNo(run.IsFinished)}")
                .AppendLine($"  Restart requested: {YesNo(run.RebootRequested)}")
                .AppendLine($"  Apps: {run.Items.Count}");
            foreach (var group in run.Items.GroupBy(item => item.Status).OrderBy(group => group.Key))
            {
                text.AppendLine($"    {group.Key}: {group.Count()}");
            }
        }
        else
        {
            text.AppendLine("  None saved");
        }

        return text.ToString();
    }

    /// <summary>
    /// One log line made safe to share: personal folders become tokens, the account and PC names are
    /// hidden, any other drive or network path becomes <c>&lt;path&gt;</c>, and a path inside a personal
    /// folder keeps only its token, except CTRL+Z's own data folder.
    /// </summary>
    public static string CleanLogLine(string line, PathPrivacy privacy)
    {
        ArgumentNullException.ThrowIfNull(privacy);
        return HidePersonalPaths(LogPrivacy.RedactPaths(privacy.ForLog(line)));
    }

    private const string OwnFolder = @"%LOCALAPPDATA%\CTRL+Z\";

    // A path runs to the end of the line, so a kept CTRL+Z path is searched again for later ones.
    private static string HidePersonalPaths(string text)
    {
        var match = PersonalPath().Match(text);
        if (!match.Success)
        {
            return text;
        }

        var before = text[..match.Index];
        if (match.Value.StartsWith(OwnFolder, StringComparison.OrdinalIgnoreCase))
        {
            return before + match.Value[..OwnFolder.Length] + HidePersonalPaths(match.Value[OwnFolder.Length..]);
        }

        return before + match.Groups["token"].Value + @"\<path>";
    }

    /// <summary>
    /// Writes the bundle to <paramref name="zipPath"/>: the summary, a README saying what is and is
    /// not inside, and up to <see cref="MaxLogFiles"/> recent CTRL+Z logs, each cleaned line by line.
    /// </summary>
    public static void Write(string zipPath, DiagnosticsInput input, string logsFolder, PathPrivacy privacy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        var logs = RecentLogs(logsFolder);
        var temp = zipPath + ".partial";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                AddText(zip, ReadMeEntry, ReadMe(logs.Count));
                AddText(zip, SummaryEntry, Summary(input));
                foreach (var log in logs)
                {
                    AddText(zip, "logs/" + Path.GetFileName(log), CleanLog(log, privacy));
                }
            }

            File.Move(temp, zipPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    /// <summary>The newest <c>ctrlz-*.log</c> files in the logs folder, newest first.</summary>
    public static IReadOnlyList<string> RecentLogs(string logsFolder)
    {
        if (string.IsNullOrWhiteSpace(logsFolder) || !Directory.Exists(logsFolder))
        {
            return [];
        }

        return new DirectoryInfo(logsFolder)
            .EnumerateFiles("ctrlz-*.log", SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(MaxLogFiles)
            .Select(file => file.FullName)
            .ToList();
    }

    private static string CleanLog(string path, PathPrivacy privacy)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = new Queue<string>();
            while (reader.ReadLine() is { } line)
            {
                lines.Enqueue(line);
                if (lines.Count > MaxLinesPerLog)
                {
                    lines.Dequeue();
                }
            }

            var text = new StringBuilder();
            foreach (var line in lines)
            {
                text.AppendLine(CleanLogLine(line, privacy));
            }

            return text.ToString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "This log could not be read: " + ex.GetType().Name + Environment.NewLine;
        }
    }

    private static string ReadMe(int logCount) =>
        "CTRL+Z diagnostics" + Environment.NewLine + Environment.NewLine +
        "Made on request from Settings. Nothing was sent anywhere: share this file only if you want to." + Environment.NewLine + Environment.NewLine +
        "Inside:" + Environment.NewLine +
        "- summary.txt: versions, settings switches and counts." + Environment.NewLine +
        $"- logs/: the {logCount} most recent CTRL+Z log file(s), last {MaxLinesPerLog} lines each, with personal" + Environment.NewLine +
        "  folders, file paths and the account and PC names removed." + Environment.NewLine + Environment.NewLine +
        "Not inside: profiles, app lists, notes, post-install commands, browser data, saved folders." + Environment.NewLine +
        "App names can appear in the logs. Open the files to check before you share them." + Environment.NewLine;

    private static void AddText(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static string YesNo(bool value) => value ? "yes" : "no";

    private static string Clean(string text) => LogPrivacy.RedactPaths(PathPrivacy.Current.ForLog(text));

    [GeneratedRegex(@"(?<token>%(?:USERPROFILE|APPDATA|LOCALAPPDATA|TEMP)%)\\[^""'\r\n]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PersonalPath();
}
