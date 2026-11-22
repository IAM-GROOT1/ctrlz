// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Install;

/// <summary>What the approval record says about one command right now.</summary>
public enum ApprovalState
{
    /// <summary>Never approved on this PC.</summary>
    None = 0,

    /// <summary>The exact current text was approved.</summary>
    Current = 1,

    /// <summary>This command slot was approved once, but its text has changed since.</summary>
    Outdated = 2
}

/// <summary>
/// Remembers which command texts a human actually reviewed, keyed by (profile, app, command slot)
/// and holding hashes only. It lives in the app's state folder, never in the profile file. Editing
/// a command changes its hash, so the approval stops matching and the slot shows "needs review".
/// Records written before slots existed (a flat list per profile) still approve exactly the text
/// they hashed, and nothing else.
/// </summary>
public sealed class PostInstallApprovalStore
{
    private const int CurrentVersion = 2;
    private const string DefaultCommandId = "default";

    private readonly string _path;
    private Dictionary<string, Dictionary<string, string>> _slots = new(StringComparer.Ordinal);
    private Dictionary<string, List<string>> _legacy = new(StringComparer.Ordinal);

    public PostInstallApprovalStore(string path)
    {
        _path = path;
        Load();
    }

    /// <summary>A stable hash of everything that makes a command what it is (not its slot id).</summary>
    public static string HashOf(PostInstallCommand command)
    {
        var text = string.Join(
            "\u001f",
            command.Executable ?? string.Empty,
            string.Join("\u001e", command.Arguments ?? new List<string>()),
            command.WorkingDirectory ?? string.Empty,
            command.RequiresElevation ? "elevated" : "normal");

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    /// <summary>
    /// The key of one command slot within a profile. Both ids come from the profile file, so they
    /// are length-prefixed: no choice of ids can make two different slots share a key.
    /// </summary>
    public static string SlotKey(string appId, PostInstallCommand command)
    {
        var commandId = string.IsNullOrWhiteSpace(command.Id) ? DefaultCommandId : command.Id;
        return $"{appId.Length}:{appId}/{commandId.Length}:{commandId}";
    }

    /// <summary>True only when this exact text was approved for this slot (or by a pre-slot record).</summary>
    public bool IsApproved(string profileId, string appId, PostInstallCommand command) =>
        StateOf(profileId, appId, command) == ApprovalState.Current;

    public ApprovalState StateOf(string profileId, string appId, PostInstallCommand command)
    {
        var hash = HashOf(command);

        // Once a slot has its own record it alone decides: an old pre-slot hash can never override
        // "this slot changed since it was approved".
        if (_slots.TryGetValue(profileId, out var slots) &&
            slots.TryGetValue(SlotKey(appId, command), out var approved))
        {
            return string.Equals(approved, hash, StringComparison.Ordinal)
                ? ApprovalState.Current
                : ApprovalState.Outdated;
        }

        // Pre-slot records approve exactly their text, as they did before slots existed.
        return LegacyContains(profileId, hash) ? ApprovalState.Current : ApprovalState.None;
    }

    /// <summary>Approves the current text of exactly this slot. Writes only this store's file.</summary>
    public void Approve(string profileId, string appId, PostInstallCommand command)
    {
        if (!_slots.TryGetValue(profileId, out var slots))
        {
            slots = new Dictionary<string, string>(StringComparer.Ordinal);
            _slots[profileId] = slots;
        }

        var key = SlotKey(appId, command);
        var hash = HashOf(command);
        if (slots.TryGetValue(key, out var existing) && string.Equals(existing, hash, StringComparison.Ordinal))
        {
            return;
        }

        slots[key] = hash;
        Save();
    }

    public void Forget(string profileId)
    {
        var removed = _slots.Remove(profileId) | _legacy.Remove(profileId);
        if (removed)
        {
            Save();
        }
    }

    private static Dictionary<string, List<string>> WithoutNulls(Dictionary<string, List<string>>? source) =>
        new(
            (source ?? new())
                .Where(pair => pair.Value is not null)
                .ToDictionary(pair => pair.Key, pair => pair.Value.Where(hash => hash is not null).ToList()),
            StringComparer.Ordinal);

    private bool LegacyContains(string profileId, string hash) =>
        _legacy.TryGetValue(profileId, out var hashes) && hashes.Contains(hash, StringComparer.Ordinal);

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var json = File.ReadAllText(_path);
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("version", out var version))
            {
                // Only the version this code writes is understood; anything else approves nothing.
                if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != CurrentVersion)
                {
                    return;
                }

                var file = JsonSerializer.Deserialize<ApprovalFile>(json, CtrlZ.Core.Infrastructure.JsonDefaults.Profile);
                _slots = new Dictionary<string, Dictionary<string, string>>(
                    (file?.Slots ?? new())
                        .Where(pair => pair.Value is not null)
                        .ToDictionary(
                            pair => pair.Key,
                            pair => new Dictionary<string, string>(
                                pair.Value!.Where(slot => slot.Value is not null).ToDictionary(slot => slot.Key, slot => slot.Value),
                                StringComparer.Ordinal),
                            StringComparer.Ordinal),
                    StringComparer.Ordinal);
                _legacy = WithoutNulls(file?.Legacy);
                return;
            }

            // Version 1: { profileId: [hashes] }. Kept read-only: each hash approves exactly its text.
            _legacy = WithoutNulls(JsonSerializer.Deserialize<Dictionary<string, List<string>>>(
                json, CtrlZ.Core.Infrastructure.JsonDefaults.Profile));
        }
        catch (Exception)
        {
            // Unreadable approvals mean nothing is approved, which fails safe.
            _slots = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            _legacy = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        }
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var file = new ApprovalFile
            {
                Version = CurrentVersion,
                Slots = _slots,
                Legacy = _legacy.Count == 0 ? null : _legacy
            };

            File.WriteAllText(_path, JsonSerializer.Serialize(file, CtrlZ.Core.Infrastructure.JsonDefaults.Profile));
        }
        catch (Exception)
        {
            // Saving is best effort; a lost approval only means the user reviews again.
        }
    }

    private sealed class ApprovalFile
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("slots")]
        public Dictionary<string, Dictionary<string, string>>? Slots { get; set; }

        [JsonPropertyName("legacy")]
        public Dictionary<string, List<string>>? Legacy { get; set; }
    }
}
