// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Models;
using CtrlZ.Core.Services;

namespace CtrlZ.Core.Tests;

public class ProfileExportTests
{
    private static AppProfile Profile() => new()
    {
        Name = "Gaming PC",
        Apps =
        [
            new AppEntry { Id = "a", Name = "Steam", PackageId = "Valve.Steam", Notes = "licence ABCD-1234" },
            new AppEntry { Id = "b", Name = "Tool", Source = AppSource.LocalFile, LocalPath = @"D:\Installers\tool.exe", Enabled = false },
            new AppEntry
            {
                Id = "c", Name = "Git", PackageId = "Git.Git",
                PostInstall = new PostInstallCommand { Executable = "git.exe", Arguments = ["config", "--global", "core.autocrlf", "true"] }
            }
        ]
    };

    [Fact]
    public void The_summary_names_what_needs_attention_on_the_other_pc()
    {
        var summary = ProfileExport.Summarize(Profile());

        Assert.Equal(3, summary.AppCount);
        Assert.Equal(2, summary.EnabledCount);
        Assert.Equal(["Tool"], summary.LocalInstallers);
        Assert.Equal(1, summary.CommandCount);
        Assert.Equal(1, summary.NoteCount);

        var text = summary.Describe();
        Assert.Contains("3 apps (2 turned on)", text);
        Assert.Contains("Tool", text);
        Assert.Contains("stay blocked until you review", text);
        Assert.Contains("licence keys", text);
        Assert.DoesNotContain("ABCD", text);
    }

    [Fact]
    public void A_plain_profile_gets_a_short_summary()
    {
        var text = ProfileExport.Summarize(new AppProfile { Apps = [new AppEntry { Name = "7-Zip", PackageId = "7zip.7zip" }] }).Describe();

        Assert.StartsWith("1 app (1 turned on)", text);
        Assert.DoesNotContain("\n", text);
    }

    [Fact]
    public void Notes_are_left_out_unless_chosen_and_the_open_profile_is_untouched()
    {
        var profile = Profile();

        var without = ProfileExport.ForExport(profile, includeNotes: false);
        var with = ProfileExport.ForExport(profile, includeNotes: true);

        Assert.All(without.Apps, app => Assert.Null(app.Notes));
        Assert.Equal("licence ABCD-1234", with.Apps[0].Notes);
        Assert.Equal("licence ABCD-1234", profile.Apps[0].Notes);
        Assert.NotNull(without.Apps[2].PostInstall);
        Assert.Equal(profile.Id, without.Id);
    }

    [Fact]
    public void An_export_without_notes_imports_with_everything_else_intact()
    {
        using var temp = new TestDirectory();
        var paths = new AppPaths(temp.PathOf("data"));
        paths.EnsureCreated();
        var store = new ProfileStore(paths, TestFactory.Log);
        var file = temp.PathOf("export.json");

        store.SaveAs(ProfileExport.ForExport(Profile(), includeNotes: false), file);
        var json = File.ReadAllText(file);
        var loaded = store.Load(file);

        Assert.DoesNotContain("ABCD", json);
        Assert.Equal(["Steam", "Tool", "Git"], loaded.Apps.Select(app => app.Name));
        Assert.Equal(@"D:\Installers\tool.exe", loaded.Apps[1].LocalPath);
        Assert.False(loaded.Apps[1].Enabled);
        Assert.Equal(["config", "--global", "core.autocrlf", "true"], loaded.Apps[2].PostInstall!.Arguments);
    }
}
