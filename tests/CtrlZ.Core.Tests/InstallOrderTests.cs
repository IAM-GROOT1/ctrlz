// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Install;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Tests;

public class InstallOrderTests
{
    private static AppEntry App(string id, string name, string? packageId = null, params string[] after) => new()
    {
        Id = id,
        Name = name,
        PackageId = packageId,
        AfterAppIds = after.ToList(),
        Enabled = true
    };

    private static AppProfile Profile(params AppEntry[] apps) => new() { Apps = apps.ToList() };

    private static DependencyCatalog Catalog() => DependencyCatalog.LoadEmbedded();

    [Fact]
    public void The_embedded_catalog_loads_from_data()
    {
        var catalog = Catalog();

        Assert.True(catalog.Dependencies.Count >= 4);
        Assert.Contains(catalog.Dependencies, dependency => dependency.Id == "vcredist.x64");
    }

    [Fact]
    public void A_dependency_is_matched_by_package_id_and_by_name()
    {
        var catalog = Catalog();

        var byId = catalog.For(App("a", "Steam", "Valve.Steam"));
        var byName = catalog.For(App("b", "OBS Studio"));
        var unrelated = catalog.For(App("c", "Notepad++", "Notepad++.Notepad++"));

        Assert.Contains(byId, dependency => dependency.Id == "vcredist.x64");
        Assert.Contains(byName, dependency => dependency.Id == "vcredist.x64");
        Assert.Empty(unrelated);
    }

    [Fact]
    public void Dependencies_come_before_the_apps_that_need_them()
    {
        var result = new InstallOrderPlanner(Catalog()).Plan(Profile(App("steam", "Steam", "Valve.Steam")));

        var names = result.Ordered.Select(app => app.Name).ToList();
        var dependencyIndex = names.FindIndex(name => name.Contains("Visual C++", StringComparison.OrdinalIgnoreCase));
        var steamIndex = names.IndexOf("Steam");

        Assert.True(dependencyIndex >= 0, "the dependency should be added");
        Assert.True(dependencyIndex < steamIndex, "the dependency must come first");
        // Steam needs both the x64 and the x86 redistributable.
        Assert.Equal(2, result.AutoAdded.Count);
        Assert.All(result.AutoAdded, app => Assert.True(app.IsAutoDependency));
    }

    [Fact]
    public void An_install_after_reference_is_honoured()
    {
        var result = new InstallOrderPlanner(Catalog()).Plan(Profile(
            App("b", "Second", after: "a"),
            App("a", "First")));

        var names = result.Ordered.Select(app => app.Name).ToList();
        Assert.True(names.IndexOf("First") < names.IndexOf("Second"));
    }

    [Fact]
    public void A_cycle_is_reported_instead_of_hanging()
    {
        var result = new InstallOrderPlanner().Plan(Profile(
            App("a", "A", after: "b"),
            App("b", "B", after: "a")));

        Assert.False(result.IsOrderable);
        Assert.Contains("A", result.Cycles[0]);
        Assert.Contains("B", result.Cycles[0]);
    }

    [Fact]
    public void A_missing_reference_is_reported_and_ignored()
    {
        var result = new InstallOrderPlanner().Plan(Profile(App("a", "A", after: "ghost")));

        Assert.True(result.IsOrderable);
        Assert.Single(result.MissingReferences);
        Assert.Contains("ghost", result.MissingReferences[0]);
        Assert.Single(result.Ordered);
    }

    [Fact]
    public void A_failed_dependency_skips_its_dependents_only()
    {
        var profile = Profile(
            App("steam", "Steam", "Valve.Steam"),
            App("other", "Unrelated"));

        var result = new InstallOrderPlanner(Catalog()).Plan(profile, failedAppIds: new[] { "dep:vcredist.x64" });

        Assert.Contains(result.Skipped, skipped => skipped.Name == "Steam");
        Assert.Contains(result.Ordered, app => app.Name == "Unrelated");
        Assert.DoesNotContain(result.Ordered, app => app.Name == "Steam");
        Assert.Contains(result.Skipped, skipped => skipped.Reason.Contains("failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_cycle_stops_its_members_and_their_dependents_but_not_unrelated_apps()
    {
        var result = new InstallOrderPlanner().Plan(Profile(
            App("a", "A", after: "b"),
            App("b", "B", after: "a"),
            App("c", "C", after: "a"),
            App("d", "D")));

        Assert.Equal(new[] { "D" }, result.Ordered.Select(app => app.Name));
        Assert.Equal(new[] { "a", "b", "c" }, result.Skipped.Select(skipped => skipped.AppId).OrderBy(id => id));
        Assert.Single(result.Cycles);
    }

    [Fact]
    public void A_self_reference_is_a_cycle()
    {
        var result = new InstallOrderPlanner().Plan(Profile(App("a", "A", after: "a")));

        Assert.False(result.IsOrderable);
        Assert.Empty(result.Ordered);
    }

    [Fact]
    public void A_failure_skips_dependents_transitively()
    {
        var result = new InstallOrderPlanner().Plan(
            Profile(App("c", "C"), App("b", "B", after: "c"), App("a", "A", after: "b"), App("x", "X")),
            failedAppIds: new[] { "c" });

        Assert.Equal(new[] { "a", "b" }, result.Skipped.Select(skipped => skipped.AppId).OrderBy(id => id));
        Assert.Contains(result.Ordered, app => app.Id == "x");
    }

    [Fact]
    public void An_app_already_in_the_profile_satisfies_a_dependency_instead_of_a_duplicate()
    {
        var result = new InstallOrderPlanner(Catalog()).Plan(Profile(
            App("steam", "Steam", "Valve.Steam"),
            App("mine", "My VC++ x64", "Microsoft.VCRedist.2015+.x64")));

        Assert.DoesNotContain(result.AutoAdded, app => app.PackageId == "Microsoft.VCRedist.2015+.x64");
        Assert.Contains("mine", result.Dependencies["steam"]);
        var names = result.Ordered.Select(app => app.Id).ToList();
        Assert.True(names.IndexOf("mine") < names.IndexOf("steam"));
    }

    [Fact]
    public void A_disabled_dependency_counts_as_removed_and_is_not_added_back()
    {
        var removed = App("dep:vcredist.x64", "Visual C++ x64", "Microsoft.VCRedist.2015+.x64");
        removed.IsAutoDependency = true;
        removed.Enabled = false;

        var result = new InstallOrderPlanner(Catalog()).Plan(Profile(App("steam", "Steam", "Valve.Steam"), removed));

        Assert.DoesNotContain(result.Ordered, app => app.PackageId == "Microsoft.VCRedist.2015+.x64");
        Assert.DoesNotContain("dep:vcredist.x64", result.Dependencies["steam"]);
        Assert.Contains(result.Ordered, app => app.Id == "steam");
    }

    [Fact]
    public void Auto_added_dependencies_are_marked_and_listed_once()
    {
        var result = new InstallOrderPlanner(Catalog()).Plan(Profile(
            App("steam", "Steam", "Valve.Steam"),
            App("obs", "OBS Studio", "OBSProject.OBSStudio")));

        Assert.All(result.AutoAdded, app => Assert.True(app.IsAutoDependency));
        Assert.Equal(result.AutoAdded.Count, result.AutoAdded.Select(app => app.PackageId).Distinct().Count());
        Assert.Equal(result.AutoAdded.Count + 2, result.Ordered.Count);
    }
}
