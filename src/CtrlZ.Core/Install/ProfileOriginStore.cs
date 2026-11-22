// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

namespace CtrlZ.Core.Install;

/// <summary>Where a profile came from. Kept outside the profile file so a shared or edited JSON
/// cannot claim to be local.</summary>
public enum ProfileOrigin
{
    /// <summary>Created or last saved on this machine, and its content still matches that save.</summary>
    Local = 0,

    /// <summary>Imported from a file, a link, a catalog or a command line: commands start disabled.</summary>
    Imported = 1,

    /// <summary>
    /// No usable record: missing entry, missing file, unreadable or corrupt file, or a profile id that
    /// was never recorded. Treated exactly like <see cref="Imported"/> by the gate, i.e. blocked.
    /// </summary>
    Unknown = 2
}

/// <summary>One remembered profile: its origin and the hash of the content that was last saved locally.</summary>
public sealed class ProfileOriginEntry
{
    public ProfileOrigin Origin { get; set; } = ProfileOrigin.Unknown;

    /// <summary>SHA-256 of the profile JSON as this machine last wrote it. Null when never saved here.</summary>
    public string? LastSavedHash { get; set; }
}

/// <summary>
/// Remembers the origin of each profile on this machine. The value lives in the app's state folder,
/// never in the profile JSON, so flipping a flag inside an imported file has no effect.
/// </summary>
public sealed class ProfileOriginStore
{
    private readonly string _path;
    private readonly CtrlZ.Core.Logging.IAppLog? _log;
    private Dictionary<string, ProfileOriginEntry> _entries = new(StringComparer.Ordinal);

    public ProfileOriginStore(string path, CtrlZ.Core.Logging.IAppLog? log = null)
    {
        _path = path;
        _log = log;
        Load();
    }

    /// <summary>True only when an entry exists and is readable.</summary>
    public bool HasEntry(string profileId) => _entries.ContainsKey(profileId);

    /// <summary>
    /// The origin. Anything other than an explicit <see cref="ProfileOrigin.Local"/> entry is unknown:
    /// a missing id, a deleted file, or a corrupt file all land here.
    /// </summary>
    public ProfileOrigin Get(string profileId) =>
        _entries.TryGetValue(profileId, out var entry) ? entry.Origin : ProfileOrigin.Unknown;

    /// <summary>The hash recorded when this profile was last saved locally, if any.</summary>
    public string? GetLastSavedHash(string profileId) =>
        _entries.TryGetValue(profileId, out var entry) ? entry.LastSavedHash : null;

    public void Set(string profileId, ProfileOrigin origin, string? lastSavedHash = null)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return;
        }

        _entries[profileId] = new ProfileOriginEntry { Origin = origin, LastSavedHash = lastSavedHash };
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var json = File.ReadAllText(_path);
            var loaded = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, ProfileOriginEntry>>(
                json,
                CtrlZ.Core.Infrastructure.JsonDefaults.Profile);

            if (loaded is not null)
            {
                _entries = new Dictionary<string, ProfileOriginEntry>(loaded, StringComparer.Ordinal);
            }
        }
        catch (Exception)
        {
            // A corrupt or unreadable file leaves no entries, which means Unknown for every profile.
            _entries = new Dictionary<string, ProfileOriginEntry>(StringComparer.Ordinal);
            _log?.Write(CtrlZ.Core.Logging.LogLevel.Warning, "Could not read profile origins; every profile is treated as untrusted.");
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

            File.WriteAllText(
                _path,
                System.Text.Json.JsonSerializer.Serialize(_entries, CtrlZ.Core.Infrastructure.JsonDefaults.Profile));
        }
        catch (Exception)
        {
            // Origin tracking must never take the app down; worst case the profile is untrusted.
        }
    }
}
