// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Text.Json;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Services;

/// <summary>Reads and writes portable profile JSON documents with atomic writes.</summary>
public sealed class ProfileStore
{
    private readonly AppPaths _paths;
    private readonly IAppLog _log;
    private readonly object _gate = new();

    public ProfileStore(AppPaths paths, IAppLog log)
    {
        _paths = paths;
        _log = log;
        Directory.CreateDirectory(_paths.Profiles);
    }

    public string ProfilesFolder => _paths.Profiles;

    public IReadOnlyList<ProfileSummary> List()
    {
        lock (_gate)
        {
            var summaries = new List<ProfileSummary>();
            foreach (var file in Directory.EnumerateFiles(_paths.Profiles, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var profile = LoadFile(file);
                    summaries.Add(new ProfileSummary(file, profile.Id, profile.Name, profile.Apps.Count, profile.UpdatedUtc));
                }
                catch (Exception ex)
                {
                    _log.Write(LogLevel.Warning, $"Skipping unreadable profile '{Path.GetFileName(file)}'.", ex);
                }
            }

            return summaries.OrderByDescending(s => s.UpdatedUtc).ToArray();
        }
    }

    public AppProfile Load(string path)
    {
        lock (_gate)
        {
            return LoadFile(path);
        }
    }

    public AppProfile? LoadById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var match = List().FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
        return match is null ? null : Load(match.Path);
    }

    /// <summary>Saves into the app's profile folder, reusing the file that already holds this profile.</summary>
    public string SaveToStore(AppProfile profile)
    {
        lock (_gate)
        {
            var existing = List().FirstOrDefault(s => s.Id == profile.Id);
            var path = existing?.Path
                       ?? Path.Combine(_paths.Profiles, $"{FileUtil.SafeFileName(profile.Name)}-{profile.Id[..Math.Min(8, profile.Id.Length)]}.json");
            SaveFile(profile, path);
            return path;
        }
    }

    public void SaveAs(AppProfile profile, string destinationPath)
    {
        lock (_gate)
        {
            SaveFile(profile, destinationPath);
        }
    }

    public AppProfile Import(string sourcePath)
    {
        AppProfile profile;
        lock (_gate)
        {
            profile = LoadFile(sourcePath);
        }

        // Keep imported profiles unique so importing twice cannot overwrite work.
        var existing = List();
        if (existing.Any(s => s.Id == profile.Id))
        {
            profile.Id = Guid.NewGuid().ToString("n");
            profile.Name = $"{profile.Name} (imported)";
        }
        else if (existing.Any(s => string.Equals(s.Name, profile.Name, StringComparison.OrdinalIgnoreCase)))
        {
            profile.Name = $"{profile.Name} (imported)";
        }

        profile.CreatedUtc = DateTimeOffset.UtcNow;
        profile.Touch();
        SaveToStore(profile);
        return profile;
    }

    public AppProfile Create(string name, string? description = null)
    {
        var profile = new AppProfile
        {
            Name = string.IsNullOrWhiteSpace(name) ? "My apps" : name.Trim(),
            Description = description
        };
        SaveToStore(profile);
        return profile;
    }

    public void Delete(string path)
    {
        lock (_gate)
        {
            if (!FileUtil.IsUnderDirectory(path, _paths.Profiles))
            {
                throw new InvalidOperationException("Only profiles in the CTRL+Z profile folder can be deleted here.");
            }

            File.Delete(path);
        }
    }

    private AppProfile LoadFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Profile file not found.", path);
        }

        var json = File.ReadAllText(path);
        var profile = JsonSerializer.Deserialize<AppProfile>(json, JsonDefaults.Profile)
                      ?? throw new InvalidDataException("The profile is empty or not valid JSON.");
        if (profile.SchemaVersion > AppProfile.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"This profile uses schema {profile.SchemaVersion}; this build supports up to {AppProfile.CurrentSchemaVersion}.");
        }

        profile.Normalize();
        return profile;
    }

    private void SaveFile(AppProfile profile, string path)
    {
        profile.Normalize();
        profile.Touch();
        var json = JsonSerializer.Serialize(profile, JsonDefaults.Profile);
        FileUtil.AtomicWriteAllText(path, json);
        _log.Write(LogLevel.Info, $"Saved profile '{profile.Name}' ({profile.Apps.Count} apps) to {path}.");
    }
}
