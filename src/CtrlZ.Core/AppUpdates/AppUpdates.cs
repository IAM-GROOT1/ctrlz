// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Localization;
using System.Text.Json;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Services;

namespace CtrlZ.Core.AppUpdates;

/// <summary>An installed app that winget can update.</summary>
/// <param name="RequiresExplicit">winget lists it as pinned or "requires explicit targeting":
/// it is never part of "update all", only updated when the user picks it.</param>
public sealed record AppUpdate(
    string Name,
    string Id,
    string? InstalledVersion,
    string AvailableVersion,
    string Source,
    bool RequiresExplicit);

public enum AppUpdateOutcomeKind
{
    Updated,
    UpdatedRestartNeeded,
    AlreadyCurrent,
    Failed,
    Cancelled
}

public sealed record AppUpdateOutcome(AppUpdate Update, AppUpdateOutcomeKind Kind, string Message);

/// <summary>Reads <c>winget upgrade</c> output and maps its exit codes. Pure, so it is fully testable.</summary>
public static class AppUpdateParser
{
    private const int UpdateNotApplicable = unchecked((int)0x8A15002B);
    private const int RebootRequiredToFinish = unchecked((int)0x8A150109);
    private const int RebootRequiredToInstall = unchecked((int)0x8A15010A);

    /// <summary>
    /// The main table lists updates "update all" may apply. A second table after the sentence
    /// about explicit targeting lists pinned packages, which are kept but marked.
    /// </summary>
    public static IReadOnlyList<AppUpdate> Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var marker = output.IndexOf("explicit targeting", StringComparison.OrdinalIgnoreCase);
        var cut = marker >= 0 ? output.LastIndexOf('\n', marker) : -1;
        var main = marker < 0 ? output : output[..(cut >= 0 ? cut : marker)];
        var pinned = marker >= 0 ? output[marker..] : string.Empty;

        var updates = new List<AppUpdate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string table, bool requiresExplicit)
        {
            foreach (var package in WingetTableParser.ParseList(table))
            {
                if (string.IsNullOrWhiteSpace(package.AvailableVersion) || !seen.Add(package.Id))
                {
                    continue;
                }

                updates.Add(new AppUpdate(
                    package.Name,
                    package.Id,
                    package.Version,
                    package.AvailableVersion!,
                    package.Source ?? "winget",
                    requiresExplicit));
            }
        }

        Add(main, requiresExplicit: false);
        if (pinned.Length > 0)
        {
            Add(pinned, requiresExplicit: true);
        }

        return updates;
    }

    public static AppUpdateOutcome Outcome(AppUpdate update, int exitCode, string? output) => exitCode switch
    {
        0 => new(update, AppUpdateOutcomeKind.Updated, CoreText.Format("AppUpdates_UpdatedTo", update.AvailableVersion)),
        RebootRequiredToFinish or RebootRequiredToInstall =>
            new(update, AppUpdateOutcomeKind.UpdatedRestartNeeded, CoreText.Get("AppUpdates_UpdatedRestartWindowsToFinish")),
        UpdateNotApplicable => new(update, AppUpdateOutcomeKind.AlreadyCurrent, CoreText.Get("AppUpdates_AlreadyUpToDate")),
        _ => new(update, AppUpdateOutcomeKind.Failed, CoreText.Format("AppUpdates_WingetCouldNotUpdateIt", exitCode))
    };
}

/// <summary>
/// "Ignore this version": hides one available version of one app until a newer one appears.
/// Stored in the state folder; unreadable means nothing is ignored.
/// </summary>
public sealed class AppUpdateIgnoreStore
{
    public const string FileName = "app-update-ignores.json";

    private readonly string _path;
    private Dictionary<string, string> _ignored = new(StringComparer.OrdinalIgnoreCase);

    public AppUpdateIgnoreStore(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(_path) &&
                JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path), JsonDefaults.Profile) is { } loaded)
            {
                _ignored = new Dictionary<string, string>(
                    loaded.Where(pair => !string.IsNullOrWhiteSpace(pair.Value)),
                    StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception)
        {
            _ignored = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public bool IsIgnored(AppUpdate update) =>
        _ignored.TryGetValue(update.Id, out var version) &&
        string.Equals(version, update.AvailableVersion, StringComparison.OrdinalIgnoreCase);

    public void Ignore(AppUpdate update)
    {
        _ignored[update.Id] = update.AvailableVersion;
        Save();
    }

    public void Unignore(string id)
    {
        if (_ignored.Remove(id))
        {
            Save();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_ignored, JsonDefaults.Profile));
        }
        catch (Exception)
        {
            // Best effort: a lost entry only shows the update again.
        }
    }
}

/// <summary>
/// Applies chosen updates one after another. The caller holds the install lock; this only runs
/// the upgrades, reports each outcome and stops cleanly when cancelled.
/// </summary>
public sealed class AppUpdateRunner
{
    private readonly Func<string, Action<string>, CancellationToken, Task<(int ExitCode, string Output)>> _upgrade;

    /// <param name="upgrade">Runs one winget upgrade for an id; returns its exit code and output.</param>
    public AppUpdateRunner(Func<string, Action<string>, CancellationToken, Task<(int ExitCode, string Output)>> upgrade)
    {
        _upgrade = upgrade;
    }

    /// <summary>Chooses what "update all" means: everything listed, except pinned and ignored.</summary>
    public static IReadOnlyList<AppUpdate> ForUpdateAll(IEnumerable<AppUpdate> updates, AppUpdateIgnoreStore ignores) =>
        updates.Where(update => !update.RequiresExplicit && !ignores.IsIgnored(update)).ToList();

    public async Task<IReadOnlyList<AppUpdateOutcome>> RunAsync(
        IReadOnlyList<AppUpdate> updates,
        IProgress<AppUpdateOutcome>? progress,
        Action<string>? onLine,
        CancellationToken cancellationToken)
    {
        var outcomes = new List<AppUpdateOutcome>();
        foreach (var update in updates)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                outcomes.Add(new AppUpdateOutcome(update, AppUpdateOutcomeKind.Cancelled, CoreText.Get("AppUpdates_NotStartedCancelled")));
                continue;
            }

            AppUpdateOutcome outcome;
            try
            {
                var (exitCode, output) = await _upgrade(update.Id, onLine ?? (_ => { }), cancellationToken).ConfigureAwait(false);
                outcome = AppUpdateParser.Outcome(update, exitCode, output);
            }
            catch (OperationCanceledException)
            {
                outcome = new AppUpdateOutcome(update, AppUpdateOutcomeKind.Cancelled, CoreText.Get("AppUpdates_StoppedCancelled"));
            }
            catch (Exception ex)
            {
                outcome = new AppUpdateOutcome(update, AppUpdateOutcomeKind.Failed, CoreText.Get("AppUpdates_CouldNotStartWinget") + ex.Message);
            }

            outcomes.Add(outcome);
            progress?.Report(outcome);
        }

        return outcomes;
    }
}
