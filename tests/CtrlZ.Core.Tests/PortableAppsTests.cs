// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.AppUpdates;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Install;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Tests;

public sealed class PortableAppsTests : IDisposable
{
    private readonly TestDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private sealed class RecordingShortcuts : IShortcutWriter
    {
        public List<(string Name, string Target)> Created { get; } = new();

        public string? Create(string name, string targetExe)
        {
            Created.Add((name, targetExe));
            return "made.lnk";
        }
    }

    private static AppEntry GitHubApp(string id = "tool", string repo = "owner/tool") =>
        new() { Id = id, Name = "Tool", Source = AppSource.GitHub, GitHubRepo = repo };

    [Fact]
    public void Tracker_records_and_reloads_the_release_per_app()
    {
        var path = _temp.PathOf("state", GitHubReleaseTracker.FileName);
        new GitHubReleaseTracker(path).Record("tool", "owner/tool", "v1.2.0");

        var tracked = new GitHubReleaseTracker(path).Get("tool");

        Assert.Equal("v1.2.0", tracked?.Tag);
        Assert.Equal("owner/tool", tracked?.Repository);
        Assert.Null(new GitHubReleaseTracker(path).Get("other"));
    }

    [Fact]
    public void An_unreadable_tracker_file_tracks_nothing()
    {
        var path = _temp.Write("state/" + GitHubReleaseTracker.FileName, "{ not json");

        Assert.Null(new GitHubReleaseTracker(path).Get("tool"));
    }

    [Theory]
    [InlineData("v1.2.0", "1.2.0", true)]
    [InlineData("V2.0", "v2.0", true)]
    [InlineData("v1.2.0", "v1.3.0", false)]
    public void Tags_compare_with_or_without_a_v(string a, string b, bool same)
    {
        Assert.Equal(same, GitHubUpdateChecker.SameTag(a, b));
    }

    [Fact]
    public async Task Only_tracked_apps_with_a_newer_release_are_offered()
    {
        var tracker = new GitHubReleaseTracker(_temp.PathOf("state", GitHubReleaseTracker.FileName));
        tracker.Record("old", "owner/old", "v1.0");
        tracker.Record("current", "owner/current", "v2.0");
        tracker.Record("moved", "owner/previous-repo", "v1.0");
        var apps = new[]
        {
            GitHubApp("old", "owner/old"),
            GitHubApp("current", "owner/current"),
            GitHubApp("moved", "owner/new-repo"),
            GitHubApp("untracked", "owner/untracked"),
            new AppEntry { Id = "winget", Source = AppSource.Winget, PackageId = "A.B" }
        };

        var updates = await GitHubUpdateChecker.CheckAsync(apps, tracker,
            (repo, _) => Task.FromResult(repo == "owner/old" ? "v1.1" : "v2.0"));

        var only = Assert.Single(updates);
        Assert.Equal("old", only.App.Id);
        Assert.Equal("v1.0", only.InstalledTag);
        Assert.Equal("v1.1", only.LatestTag);
    }

    [Fact]
    public async Task A_repository_that_cannot_be_reached_is_skipped()
    {
        var tracker = new GitHubReleaseTracker(_temp.PathOf("state", GitHubReleaseTracker.FileName));
        tracker.Record("tool", "owner/tool", "v1.0");

        var updates = await GitHubUpdateChecker.CheckAsync([GitHubApp()], tracker,
            (_, _) => throw new HttpRequestException("offline"));

        Assert.Empty(updates);
    }

    [Fact]
    public void The_real_writer_creates_a_shortcut_in_the_given_folder_only()
    {
        var programs = _temp.PathOf("programs");
        var target = _temp.Write("apps/tool/tool.exe", "binary");

        var path = new StartMenuShortcutWriter(TestFactory.Log, programs).Create("Tool", target);

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.StartsWith(Path.Combine(programs, StartMenuShortcutWriter.FolderName), path);
        Assert.Null(new StartMenuShortcutWriter(TestFactory.Log, programs).Create("Missing", _temp.PathOf("nope.exe")));
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task A_portable_app_gets_a_shortcut_only_when_enabled(bool enabled, int expected)
    {
        var paths = new AppPaths(_temp.PathOf("data"));
        paths.EnsureCreated();
        var shortcuts = new RecordingShortcuts();
        var service = new FileInstallService(paths, new ProcessRunner(), TestFactory.Log, shortcuts, () => enabled);
        var exe = _temp.Write("download/tool.exe", "binary");
        var app = new AppEntry { Id = "tool", Name = "Tool", Source = AppSource.DirectUrl };

        var result = await service.InstallAsync(app, exe, GitHubAssetKind.PortableExecutable,
            new InstallContext(paths, NullInstallLogSink.Instance, TestFactory.Log, false), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(expected, shortcuts.Created.Count);
        if (expected == 1)
        {
            Assert.Equal("Tool", shortcuts.Created[0].Name);
            Assert.EndsWith("tool.exe", shortcuts.Created[0].Target);
            Assert.StartsWith(paths.Apps, shortcuts.Created[0].Target);
        }
    }
}
