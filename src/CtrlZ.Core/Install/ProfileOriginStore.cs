// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

namespace CtrlZ.Core.Install;

/// <summary>Where a profile came from. Kept outside the profile file so a shared or edited JSON
/// cannot claim to be local.</summary>
public enum ProfileOrigin
{
    /// <summary>Created or edited on this machine.</summary>
    Local = 0,

    /// <summary>Imported from a file, a link, or a catalog: its post-install commands start disabled.</summary>
    Imported = 1
}

/// <summary>
/// Remembers the origin of each profile on this machine. The value lives in the app's state folder,
/// not in the profile JSON, so flipping a flag inside an imported file has no effect.
/// </summary>
public sealed class ProfileOriginStore
{
    private readonly string _path;
    private readonly CtrlZ.Core.Logging.IAppLog? _log;
    private Dictionary<string, ProfileOrigin> _origins = new(StringComparer.Ordinal);

    public ProfileOriginStore(string path, CtrlZ.Core.Logging.IAppLog? log = null)
    {
        _path = path;
        _log = log;
        Load();
    }

    public ProfileOrigin Get(string profileId) =>
        _origins.TryGetValue(profileId, out var origin) ? origin : ProfileOrigin.Local;

    public void Set(string profileId, ProfileOrigin origin)
    {
        _origins[profileId] = origin;
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
            var loaded = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, ProfileOrigin>>(
                json,
                CtrlZ.Core.Infrastructure.JsonDefaults.Profile);
            if (loaded is not null)
            {
                _origins = new Dictionary<string, ProfileOrigin>(loaded, StringComparer.Ordinal);
            }
        }
        catch (Exception)
        {
            _log?.Write(CtrlZ.Core.Logging.LogLevel.Warning, "Could not read profile origins; treating all profiles as local.");
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
                System.Text.Json.JsonSerializer.Serialize(_origins, CtrlZ.Core.Infrastructure.JsonDefaults.Profile));
        }
        catch
        {
            // Origin tracking must never take the app down; worst case a profile is treated as local.
        }
    }
}
