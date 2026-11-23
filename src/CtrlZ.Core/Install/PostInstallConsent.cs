// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Localization;
using System.Text.Json;
using CtrlZ.Core.Infrastructure;

namespace CtrlZ.Core.Install;

/// <summary>What the user must confirm before an install whose commands may run.</summary>
public sealed record PostInstallConsentRequest(
    IReadOnlyList<CommandPreviewLine> WillRun,
    bool NeedsFirstRunConfirmation,
    IReadOnlyList<CommandPreviewLine> Risky)
{
    public bool NeedsAny => NeedsFirstRunConfirmation || Risky.Count > 0;
}

/// <summary>
/// Decides which confirmations an install needs. It never weakens the gate: commands the gate
/// blocks are not offered here at all. A first-run confirmation is asked once per command text on
/// this PC; a risky command is confirmed again on every run.
/// </summary>
public static class PostInstallConsent
{
    public static PostInstallConsentRequest Required(
        IReadOnlyList<CommandPreviewLine> lines, IReadOnlyCollection<string> confirmedHashes)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(confirmedHashes);

        var willRun = lines.Where(line => line.WillRun).ToList();
        var firstRun = willRun.Any(line => !confirmedHashes.Contains(line.Hash, StringComparer.Ordinal));
        var risky = willRun.Where(line => line.Risky).ToList();
        return new PostInstallConsentRequest(willRun, firstRun, risky);
    }

    /// <summary>
    /// Asks every confirmation the run needs, in order, and records the first-run answer. Returns
    /// false as soon as one is declined; the caller must then start nothing.
    /// </summary>
    /// <param name="confirm">Shows (title, message, danger) and returns true only on an explicit yes.</param>
    public static bool Ask(
        IReadOnlyList<CommandPreviewLine> lines,
        string profileId,
        PostInstallFirstRunStore firstRun,
        Func<string, string, bool, bool> confirm)
    {
        ArgumentNullException.ThrowIfNull(firstRun);
        ArgumentNullException.ThrowIfNull(confirm);

        var consent = Required(lines, firstRun.ConfirmedFor(profileId));

        if (consent.NeedsFirstRunConfirmation)
        {
            var list = string.Join("\n", consent.WillRun.Select(line => $"• {line.AppName}: {line.CommandText}"));
            var count = consent.WillRun.Count;
            if (!confirm(
                    CoreText.Get("PostInstallConsent_RunPostInstallCommands"),
                    CoreText.Plural("PostInstallConsent_ProfileWillRun", count, list),
                    false))
            {
                return false;
            }
        }

        foreach (var risky in consent.Risky)
        {
            if (!confirm(
                    CoreText.Get("PostInstallConsent_RunARiskyCommand"),
                    CoreText.Format("PostInstallConsent_WillRunACommandThat", risky.AppName, risky.CommandText) +
                    CoreText.Get("PostInstallConsent_ThisIsAskedOnEvery"),
                    true))
            {
                return false;
            }
        }

        // Recorded only once every prompt was accepted, so a declined run leaves no trace.
        if (consent.NeedsFirstRunConfirmation)
        {
            firstRun.Record(profileId, consent.WillRun.Select(line => line.Hash));
        }

        return true;
    }
}

/// <summary>
/// Remembers which command texts the user already agreed to run on this PC, per profile. Hashes
/// only; lives in the state folder. Unreadable means nothing is confirmed, so it asks again.
/// </summary>
public sealed class PostInstallFirstRunStore
{
    public const string FileName = "post-install-first-run.json";

    private readonly string _path;
    private Dictionary<string, List<string>> _confirmed = new(StringComparer.Ordinal);

    public PostInstallFirstRunStore(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(_path), JsonDefaults.Profile);
                if (loaded is not null)
                {
                    _confirmed = new Dictionary<string, List<string>>(loaded, StringComparer.Ordinal);
                }
            }
        }
        catch (Exception)
        {
            _confirmed = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        }
    }

    public IReadOnlyCollection<string> ConfirmedFor(string profileId) =>
        _confirmed.TryGetValue(profileId, out var hashes) ? hashes : Array.Empty<string>();

    public void Record(string profileId, IEnumerable<string> hashes)
    {
        if (!_confirmed.TryGetValue(profileId, out var list))
        {
            list = new List<string>();
            _confirmed[profileId] = list;
        }

        foreach (var hash in hashes.Where(hash => !list.Contains(hash, StringComparer.Ordinal)))
        {
            list.Add(hash);
        }

        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(_confirmed, JsonDefaults.Profile));
        }
        catch (Exception)
        {
            // Best effort: a lost record only means the user is asked again.
        }
    }
}
