// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.IO;
using System.Text.Json;
using Xunit;
using CtrlZ.App.Services;
using CtrlZ.App.ViewModels;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Install;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Models;
using CtrlZ.Core.Services;

namespace CtrlZ.App.Tests;

public sealed class AutoDependencyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ctrlz-deps-" + Guid.NewGuid().ToString("n")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private (AppState State, AppPaths Paths) NewState()
    {
        var paths = new AppPaths(_root);
        var log = NullAppLog.Instance;
        var settings = new SettingsStore(Path.Combine(_root, "settings.json"), log);
        var state = new AppState(new ProfileStore(paths, log), settings, settings.Load(), log);
        state.Initialize();
        return (state, paths);
    }

    private static InstallOrderResult Plan(AppProfile profile) =>
        new InstallOrderPlanner(DependencyCatalog.LoadEmbedded()).Plan(profile);

    [Fact]
    public void Dependencies_are_added_once_marked_and_saved_keeping_the_origin()
    {
        var (state, paths) = NewState();
        state.AddApps([new AppEntry { Id = "steam", Name = "Steam", PackageId = "Valve.Steam" }]);

        var added = state.AddAutoDependencies(Plan(state.ActiveProfile).AutoAdded);
        var again = state.AddAutoDependencies(Plan(state.ActiveProfile).AutoAdded);

        Assert.True(added > 0);
        Assert.Equal(0, again);
        Assert.Equal(added, state.ActiveProfile.Apps.Count(app => app.IsAutoDependency));
        Assert.Equal(ProfileOrigin.Local, state.OriginOf(state.ActiveProfile));
        var saved = state.Profiles.First(summary => summary.Id == state.ActiveProfile.Id).Path;
        var onDisk = JsonSerializer.Deserialize<AppProfile>(File.ReadAllText(saved), JsonDefaults.Profile)!;
        Assert.Equal(added, onDisk.Apps.Count(app => app.IsAutoDependency));
        Assert.Empty(Plan(state.ActiveProfile).AutoAdded);
        _ = paths;
    }

    [Fact]
    public void A_turned_off_dependency_is_not_added_back_and_not_installed()
    {
        var (state, _) = NewState();
        state.AddApps([new AppEntry { Id = "steam", Name = "Steam", PackageId = "Valve.Steam" }]);
        state.AddAutoDependencies(Plan(state.ActiveProfile).AutoAdded);

        foreach (var dependency in state.ActiveProfile.Apps.Where(app => app.IsAutoDependency).ToList())
        {
            state.DisableApp(dependency);
        }

        var plan = Plan(state.ActiveProfile);
        Assert.Empty(plan.AutoAdded);
        Assert.DoesNotContain(plan.Ordered, app => app.IsAutoDependency);
        Assert.Equal(0, state.AddAutoDependencies(plan.AutoAdded));
    }

    [Fact]
    public void My_apps_marks_an_auto_added_dependency()
    {
        var row = new ProfileAppRow(new AppEntry { Name = "VC++", IsAutoDependency = true, Publisher = "Microsoft" }, 1, _ => { });

        Assert.StartsWith("Added automatically", row.Meta);
    }
}
