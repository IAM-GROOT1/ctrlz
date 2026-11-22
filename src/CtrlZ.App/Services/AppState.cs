using System.IO;
using CtrlZ.Core.Install;
// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Models;
using CtrlZ.Core.Services;

namespace CtrlZ.App.Services;

/// <summary>
/// Shared profile state for every page. Pages never write profile JSON directly; they go
/// through this object so the active profile and sidebar stay consistent.
/// </summary>
public sealed class AppState : ObservableObject
{
    private readonly ProfileStore _profileStore;
    private readonly SettingsStore _settingsStore;
    private readonly IAppLog _log;
    private AppProfile _activeProfile = new();
    private bool _initialized;
    private readonly ProfileOriginStore _origins;

    public AppState(ProfileStore profileStore, SettingsStore settingsStore, AppSettings settings, IAppLog log)
    {
        _profileStore = profileStore;
        _settingsStore = settingsStore;
        Settings = settings;
        _log = log;
        _origins = new ProfileOriginStore(Path.Combine(profileStore.StateFolder, "profile-origins.json"), log);
    }

    public AppSettings Settings { get; }
    public ObservableCollection<ProfileSummary> Profiles { get; } = new();
    public AppProfile ActiveProfile => _activeProfile;
    public string ActiveProfileName => _activeProfile.Name;
    public int ActiveAppCount => _activeProfile.Apps.Count;

    /// <summary>Read-only view of where a profile came from, for the preview and review screens.</summary>
    public ProfileOrigin OriginOf(AppProfile profile) => _origins.Get(profile.Id);

    /// <summary>
    /// Records exactly the reviewed profile as created here. Only the review screen calls this, and
    /// only after the user confirmed an explicit dialog; without that flag nothing changes.
    /// </summary>
    public void MarkAsLocal(AppProfile profile, bool userConfirmed)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!userConfirmed)
        {
            return;
        }

        ProfileOriginRecorder.MarkAsLocal(_origins, profile, userConfirmed);
        ProfileContentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Loads the profile of a resumed install session through the origin recorder, so a file that
    /// changed since its last local save is treated as imported.
    /// </summary>
    public AppProfile LoadForRun(string path) =>
        ProfileOriginRecorder.LoadForUse(_origins, _profileStore.Load(path));

    public event EventHandler? ActiveProfileChanged;
    public event EventHandler? ProfileContentChanged;
    public event EventHandler? ProfilesChanged;

    public void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        var summaries = _profileStore.List().ToList();
        if (summaries.Count == 0)
        {
            var profile = _profileStore.Create("My apps");
            ProfileOriginRecorder.RecordCreated(_origins, profile);
            summaries = _profileStore.List().ToList();
            _log.Write(LogLevel.Info, $"Created initial profile '{profile.Name}'.");
        }

        ReplaceProfiles(summaries);
        var summary = summaries.FirstOrDefault(s => s.Id == Settings.ActiveProfileId) ?? summaries[0];
        LoadActive(summary);
    }

    public void ReloadProfiles()
    {
        var activeId = _activeProfile.Id;
        var summaries = _profileStore.List().ToList();
        if (summaries.Count == 0)
        {
            ProfileOriginRecorder.RecordCreated(_origins, _profileStore.Create("My apps"));
            summaries = _profileStore.List().ToList();
        }

        ReplaceProfiles(summaries);
        var activeSummary = summaries.FirstOrDefault(s => s.Id == activeId);
        if (activeSummary is null)
        {
            LoadActive(summaries[0]);
        }
    }

    public void SetActive(ProfileSummary summary)
    {
        if (summary.Id == _activeProfile.Id && _initialized)
        {
            return;
        }

        LoadActive(summary);
    }

    public AppProfile CreateProfile(string name, string? description = null)
    {
        var profile = _profileStore.Create(name, description);
        ProfileOriginRecorder.RecordCreated(_origins, profile);
        var summaries = _profileStore.List().ToList();
        ReplaceProfiles(summaries);
        var summary = summaries.First(s => s.Id == profile.Id);
        LoadActive(summary);
        return profile;
    }

    public AppProfile ImportProfile(string sourcePath)
    {
        var profile = _profileStore.Import(sourcePath);
        ProfileOriginRecorder.RecordImported(_origins, profile);
        var summaries = _profileStore.List().ToList();
        ReplaceProfiles(summaries);
        var summary = summaries.First(s => s.Id == profile.Id);
        LoadActive(summary);
        return profile;
    }

    public void DeleteProfile(ProfileSummary summary)
    {
        _profileStore.Delete(summary.Path);
        var summaries = _profileStore.List().ToList();
        if (summaries.Count == 0)
        {
            ProfileOriginRecorder.RecordCreated(_origins, _profileStore.Create("My apps"));
            summaries = _profileStore.List().ToList();
        }

        ReplaceProfiles(summaries);
        if (_activeProfile.Id == summary.Id)
        {
            LoadActive(summaries[0]);
        }
    }

    public void RenameActiveProfile(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        _activeProfile.Name = name.Trim();
        SaveActive();
        OnPropertyChanged(nameof(ActiveProfileName));
    }

    public void ExportActive(string destinationPath)
    {
        _profileStore.SaveAs(_activeProfile, destinationPath);
    }

    public void SaveActive()
    {
        if (string.IsNullOrWhiteSpace(_activeProfile.Name))
        {
            _activeProfile.Name = "My apps";
        }

        _profileStore.SaveToStore(_activeProfile);
        ProfileOriginRecorder.RecordSaved(_origins, _activeProfile);
        ReloadProfiles();
        OnPropertyChanged(nameof(ActiveProfileName));
        OnPropertyChanged(nameof(ActiveAppCount));
        ProfileContentChanged?.Invoke(this, EventArgs.Empty);
    }

    public (int Added, int Skipped) AddApps(IEnumerable<AppEntry> apps)
    {
        var existing = BuildKeys(_activeProfile.Apps);
        var added = 0;
        var skipped = 0;
        foreach (var app in apps)
        {
            var key = BuildKey(app);
            if (!existing.Add(key))
            {
                skipped++;
                continue;
            }

            _activeProfile.Apps.Add(app.Clone());
            added++;
        }

        if (added > 0)
        {
            SaveActive();
        }

        return (added, skipped);
    }

    public bool RemoveApp(AppEntry app)
    {
        var removed = _activeProfile.Apps.Remove(app);
        if (removed)
        {
            SaveActive();
        }

        return removed;
    }

    public void ReplaceApp(AppEntry original, AppEntry replacement)
    {
        var index = _activeProfile.Apps.IndexOf(original);
        if (index < 0)
        {
            return;
        }

        _activeProfile.Apps[index] = replacement;
        SaveActive();
    }

    public bool MoveApp(AppEntry app, int offset)
    {
        var index = _activeProfile.Apps.IndexOf(app);
        var destination = index + offset;
        if (index < 0 || destination < 0 || destination >= _activeProfile.Apps.Count)
        {
            return false;
        }

        _activeProfile.Apps.RemoveAt(index);
        _activeProfile.Apps.Insert(destination, app);
        SaveActive();
        return true;
    }

    public void SaveSettings()
    {
        Settings.ActiveProfileId = _activeProfile.Id;
        _settingsStore.Save(Settings);
    }

    public static string BuildKey(AppEntry app)
    {
        if (!string.IsNullOrWhiteSpace(app.PackageId) &&
            app.Source is AppSource.Winget or AppSource.MicrosoftStore)
        {
            return $"winget:{app.PackageId.Trim().ToLowerInvariant()}";
        }

        return app.Source switch
        {
            AppSource.GitHub => $"github:{app.GitHubRepo?.Trim().ToLowerInvariant()}:{app.GitHubAsset?.Trim().ToLowerInvariant()}",
            AppSource.DirectUrl => $"url:{app.DownloadUrl?.Trim().ToLowerInvariant()}",
            AppSource.LocalFile => $"file:{app.LocalPath?.Trim().ToLowerInvariant()}",
            _ => $"name:{app.Name.Trim().ToLowerInvariant()}:{app.Source}"
        };
    }

    private static HashSet<string> BuildKeys(IEnumerable<AppEntry> apps) =>
        apps.Select(BuildKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private void LoadActive(ProfileSummary summary)
    {
        try
        {
            _activeProfile = ProfileOriginRecorder.LoadForUse(_origins, _profileStore.Load(summary.Path));
        }
        catch (Exception ex)
        {
            _log.Write(LogLevel.Error, $"Could not load profile {summary.Path}; creating an empty one.", ex);
            _activeProfile = new AppProfile { Name = summary.Name };
        }

        Settings.ActiveProfileId = _activeProfile.Id;
        _settingsStore.Save(Settings);
        OnPropertyChanged(nameof(ActiveProfile));
        OnPropertyChanged(nameof(ActiveProfileName));
        OnPropertyChanged(nameof(ActiveAppCount));
        ActiveProfileChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ReplaceProfiles(IEnumerable<ProfileSummary> summaries)
    {
        Profiles.Clear();
        foreach (var summary in summaries.OrderByDescending(s => s.UpdatedUtc))
        {
            Profiles.Add(summary);
        }

        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }
}
