using System.IO;
using System.Text.Json;
// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using Xunit;
using CtrlZ.App.Services;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Install;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Models;
using CtrlZ.Core.Services;

namespace CtrlZ.App.Tests;

/// <summary>
/// One test per profile-loading route, each proving the origin that route leaves behind.
/// AppState is constructed headlessly against a temp folder; nothing here needs a window.
/// </summary>
public class ProfileRouteOriginTests : IDisposable
{
    private readonly string _root;
    private readonly IAppLog _log = NullAppLog.Instance;

    public ProfileRouteOriginTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ctrlz-routes-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // Temp cleanup only.
        }
    }

    private (AppState State, Func<string, ProfileOrigin> Origin) NewState(string name = "a")
    {
        var root = Path.Combine(_root, name);
        var paths = new AppPaths(root);
        var profiles = new ProfileStore(paths, _log);
        var settings = new SettingsStore(Path.Combine(root, "settings.json"), _log);
        var store = settings.Load();
        var state = new AppState(profiles, settings, store, _log);
        // Read the origin file fresh at assertion time: AppState writes through its own instance.
        Func<string, ProfileOrigin> origin = id =>
            new ProfileOriginStore(Path.Combine(paths.State, "profile-origins.json"), _log).Get(id);
        return (state, origin);
    }

    [Fact]
    public void Startup_load_creates_a_local_profile()
    {
        var (state, Origin) = NewState();

        state.Initialize();

        Assert.Equal(ProfileOrigin.Local, Origin(state.ActiveProfile.Id));
    }

    [Fact]
    public void CreateProfile_records_local()
    {
        var (state, Origin) = NewState();

        var profile = state.CreateProfile("Work");

        Assert.Equal(ProfileOrigin.Local, Origin(profile.Id));
    }

    [Fact]
    public void SaveActive_keeps_a_locally_created_profile_local()
    {
        var (state, Origin) = NewState();
        state.Initialize();
        var id = state.ActiveProfile.Id;

        state.SaveActive();

        Assert.Equal(ProfileOrigin.Local, Origin(id));
    }

    [Fact]
    public void ImportProfile_via_file_open_records_imported()
    {
        var source = WriteExternalProfile("shared");
        var (state, Origin) = NewState();

        var profile = state.ImportProfile(source);

        Assert.Equal(ProfileOrigin.Imported, Origin(profile.Id));
    }

    [Fact]
    public void ImportProfile_via_dialog_path_records_imported()
    {
        // The dialog hands AppState the chosen path; it is the same entry point as the file-open route.
        var source = WriteExternalProfile("dialog");
        var (state, Origin) = NewState();

        var profile = state.ImportProfile(source);

        Assert.Equal(ProfileOrigin.Imported, Origin(profile.Id));
    }

    [Fact]
    public void SetActive_loads_an_unmodified_local_profile_as_local()
    {
        var (state, Origin) = NewState();
        state.Initialize();
        var created = state.CreateProfile("Second");
        var summaries = state.Profiles.ToList();

        state.SetActive(summaries.First(summary => summary.Id == created.Id));

        Assert.Equal(ProfileOrigin.Local, Origin(created.Id));
    }

    [Fact]
    public void SetActive_loads_an_externally_edited_profile_as_imported()
    {
        var (state, Origin) = NewState();
        state.Initialize();
        var created = state.CreateProfile("Edited");
        var summary = state.Profiles.First(item => item.Id == created.Id);

        // Edit the file on disk the way another tool would.
        var stored = JsonSerializer.Deserialize<AppProfile>(File.ReadAllText(summary.Path), JsonDefaults.Profile)!;
        stored.Name = "Edited on disk";
        File.WriteAllText(summary.Path, JsonSerializer.Serialize(stored, JsonDefaults.Profile));

        // Switch away first: SetActive on the already-active profile is a no-op.
        var other = state.CreateProfile("Other");
        state.SetActive(state.Profiles.First(item => item.Id == other.Id));
        state.SetActive(summary);

        Assert.Equal(ProfileOrigin.Imported, Origin(created.Id));
    }

    [Fact]
    public void ExportActive_save_as_of_an_imported_profile_stays_imported()
    {
        var source = WriteExternalProfile("export-me");
        var (state, Origin) = NewState();
        var imported = state.ImportProfile(source);

        state.ExportActive(Path.Combine(_root, "copy.json"));

        Assert.Equal(ProfileOrigin.Imported, Origin(imported.Id));
    }

    [Fact]
    public void ExportActive_save_as_of_a_local_profile_stays_local()
    {
        var (state, Origin) = NewState();
        state.Initialize();
        var id = state.ActiveProfile.Id;

        state.ExportActive(Path.Combine(_root, "copy.json"));

        Assert.Equal(ProfileOrigin.Local, Origin(id));
    }

    /// <summary>Writes a profile the way a shared file would arrive: created elsewhere, no local history.</summary>
    private string WriteExternalProfile(string name)
    {
        var path = Path.Combine(_root, name + "-external.json");
        var profile = new AppProfile
        {
            Name = name,
            Apps = { new AppEntry { Name = "Some app", Id = "external-" + name } }
        };
        File.WriteAllText(path, JsonSerializer.Serialize(profile, JsonDefaults.Profile));
        return path;
    }
}
