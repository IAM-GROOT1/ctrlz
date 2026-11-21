// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Models;
using CtrlZ.Core.Safety;
using CtrlZ.Core.Services;

namespace CtrlZ.Core.Tests;

public class InstallPlannerTests
{
    [Fact]
    public void Only_enabled_apps_are_planned_and_order_follows_the_profile()
    {
        var profile = new AppProfile
        {
            Apps =
            [
                Entry("First", AppSource.Winget, packageId: "Vendor.First"),
                Entry("Disabled", AppSource.Winget, packageId: "Vendor.Skip", enabled: false),
                Entry("Third", AppSource.Winget, packageId: "Vendor.Third")
            ]
        };

        var plan = new InstallPlanner(fileExists: _ => false, isSigned: _ => null).Build(profile, "Test");

        Assert.Equal(2, plan.Count);
        Assert.Equal(new[] { "First", "Third" }, plan.Items.Select(item => item.Name));
        Assert.Equal(new[] { 1, 2 }, plan.Items.Select(item => item.Order));
    }

    [Fact]
    public void A_missing_local_installer_is_warned_about()
    {
        var profile = new AppProfile { Apps = [Entry("Local", AppSource.LocalFile, localPath: @"C:\nope\setup.exe")] };

        var item = new InstallPlanner(fileExists: _ => false, isSigned: _ => null).Build(profile, "Test").Items[0];

        Assert.Contains(item.Warnings, warning => warning.Contains("not on this PC", StringComparison.OrdinalIgnoreCase));
        Assert.True(item.RequiresAdmin);
    }

    [Fact]
    public void An_unsigned_local_installer_is_warned_about()
    {
        var profile = new AppProfile { Apps = [Entry("Local", AppSource.LocalFile, localPath: @"C:\apps\setup.exe")] };

        var item = new InstallPlanner(fileExists: _ => true, isSigned: _ => false).Build(profile, "Test").Items[0];

        Assert.Contains(item.Warnings, warning => warning.Contains("not code-signed", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(AppSource.DirectUrl)]
    [InlineData(AppSource.GitHub)]
    public void Remote_sources_without_a_recorded_hash_are_warned_about(AppSource source)
    {
        var profile = new AppProfile
        {
            Apps =
            [
                source == AppSource.GitHub
                    ? Entry("From GitHub", AppSource.GitHub, repo: "owner/repo")
                    : Entry("From URL", AppSource.DirectUrl, url: "https://example.com/setup.exe")
            ]
        };

        var plan = new InstallPlanner(fileExists: _ => false, isSigned: _ => null).Build(profile, "Test");

        Assert.Contains(plan.Items[0].Warnings, warning => warning.Contains("SHA-256", StringComparison.Ordinal));
        Assert.Contains(plan.PlanWarnings, warning => warning.Contains("warning", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_winget_app_without_an_id_is_warned_about()
    {
        var profile = new AppProfile { Apps = [Entry("Nameless", AppSource.Winget)] };

        var item = new InstallPlanner(fileExists: _ => false, isSigned: _ => null).Build(profile, "Test").Items[0];

        Assert.Contains(item.Warnings, warning => warning.Contains("package id", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("--silent", item.SilentArgs, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_empty_profile_explains_itself()
    {
        var plan = new InstallPlanner(fileExists: _ => false, isSigned: _ => null).Build(new AppProfile(), "Empty");

        Assert.True(plan.IsEmpty);
        Assert.Contains(plan.PlanWarnings, warning => warning.Contains("nothing enabled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_plan_always_says_dependencies_are_not_resolved()
    {
        var plan = new InstallPlanner(fileExists: _ => false, isSigned: _ => null)
            .Build(new AppProfile { Apps = [Entry("A", AppSource.Winget, packageId: "V.A")] }, "Test");

        Assert.Contains(plan.PlanWarnings, warning => warning.Contains("dependencies", StringComparison.OrdinalIgnoreCase));
    }

    internal static AppEntry Entry(
        string name,
        AppSource source,
        string? packageId = null,
        string? url = null,
        string? repo = null,
        string? localPath = null,
        bool enabled = true,
        string? hash = null) => new()
    {
        Name = name,
        Source = source,
        PackageId = packageId,
        DownloadUrl = url,
        GitHubRepo = repo,
        GitHubAsset = repo is null ? null : "setup.exe",
        LocalPath = localPath,
        ExpectedSha256 = hash,
        Enabled = enabled
    };
}

public class DryRunTests
{
    [Fact]
    public async Task A_winget_id_that_resolves_is_ready()
    {
        var app = InstallPlannerTests.Entry("Tool", AppSource.Winget, packageId: "Vendor.Tool");
        var report = await Run(
            [app],
            search: _ => [new WingetPackage("Tool", "Vendor.Tool", "1.0", "1.1")]);

        var item = Assert.Single(report.Items);
        Assert.Equal(PlanCheckStatus.Ready, item.Status);
        Assert.Contains("1.1", item.Message);
    }

    [Fact]
    public async Task A_winget_id_that_does_not_exist_fails()
    {
        var app = InstallPlannerTests.Entry("Tool", AppSource.Winget, packageId: "Vendor.Missing");
        var report = await Run([app], search: _ => []);

        Assert.Equal(PlanCheckStatus.Failed, report.Items[0].Status);
        Assert.Contains("cannot find", report.Items[0].Message);
        Assert.True(report.HasFailures);
    }

    [Fact]
    public async Task A_github_release_with_a_matching_asset_is_ready_with_its_size()
    {
        var app = InstallPlannerTests.Entry("From GitHub", AppSource.GitHub, repo: "owner/repo");
        var report = await Run(
            [app],
            latest: _ => Release("v1.0.0", new GitHubAsset("setup.exe", "https://github.com/o/r/setup.exe", 2048, null, null)));

        var item = Assert.Single(report.Items);
        Assert.Equal(PlanCheckStatus.Ready, item.Status);
        Assert.Equal(2048, item.DownloadBytes);
    }

    [Fact]
    public async Task A_release_that_still_has_the_recorded_asset_is_ready()
    {
        var app = InstallPlannerTests.Entry("From GitHub", AppSource.GitHub, repo: "owner/repo");
        var report = await Run([app], latest: _ => Release("v1.0.0", new GitHubAsset("setup.exe", "https://github.com/o/r/setup.exe", 10, null, null)));

        var item = Assert.Single(report.Items);
        Assert.Equal(PlanCheckStatus.Ready, item.Status);
        Assert.Contains("setup.exe", item.Message);
    }

    [Fact]
    public async Task A_renamed_asset_is_a_warning_that_names_both_assets()
    {
        var app = InstallPlannerTests.Entry("From GitHub", AppSource.GitHub, repo: "owner/repo");
        var report = await Run(
            [app],
            latest: _ => Release("v1.0.0", new GitHubAsset("setup-1.1.exe", "https://github.com/o/r/setup-1.1.exe", 10, null, null)));

        // The install behaviour still falls back, but the preview must not report a plain pass:
        // a different file will run than the one that was recorded.
        var item = Assert.Single(report.Items);
        Assert.Equal(PlanCheckStatus.Warning, item.Status);
        Assert.Contains("setup.exe", item.Message);
        Assert.Contains("setup-1.1.exe", item.Message);
        Assert.False(report.HasFailures);
    }

    [Fact]
    public async Task A_release_with_no_assets_at_all_fails()
    {
        var app = InstallPlannerTests.Entry("From GitHub", AppSource.GitHub, repo: "owner/repo");
        var report = await Run([app], latest: _ => Release("v1.0.0"));

        Assert.Equal(PlanCheckStatus.Failed, report.Items[0].Status);
        Assert.Contains("no asset", report.Items[0].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_reachable_url_is_ready_with_its_size()
    {
        var app = InstallPlannerTests.Entry("From URL", AppSource.DirectUrl, url: "https://example.com/setup.exe");
        var report = await Run([app], head: _ => 4096L);

        var item = Assert.Single(report.Items);
        Assert.Equal(PlanCheckStatus.Ready, item.Status);
        Assert.Equal(4096, item.DownloadBytes);
    }

    [Fact]
    public async Task A_server_that_rejects_head_is_a_warning_not_a_failure()
    {
        var app = InstallPlannerTests.Entry("From URL", AppSource.DirectUrl, url: "https://example.com/setup.exe");

        var report = await Run([app], head: _ => throw new DownloadException("method not allowed", statusCode: 405));

        Assert.Equal(PlanCheckStatus.Warning, report.Items[0].Status);
        Assert.False(report.HasFailures);
    }

    [Fact]
    public async Task An_unreachable_url_fails()
    {
        var app = InstallPlannerTests.Entry("From URL", AppSource.DirectUrl, url: "https://example.com/gone.exe");

        var report = await Run([app], head: _ => throw new DownloadException("404 Not Found", statusCode: 404));

        Assert.Equal(PlanCheckStatus.Failed, report.Items[0].Status);
    }

    [Fact]
    public async Task A_present_local_installer_is_ready()
    {
        var app = InstallPlannerTests.Entry("Local", AppSource.LocalFile, localPath: @"C:\apps\setup.exe");
        var report = await Run([app], exists: _ => true, signed: _ => true);

        Assert.Equal(PlanCheckStatus.Ready, report.Items[0].Status);
    }

    [Fact]
    public async Task An_unsigned_local_installer_warns_but_does_not_fail()
    {
        var app = InstallPlannerTests.Entry("Local", AppSource.LocalFile, localPath: @"C:\apps\setup.exe");
        var report = await Run([app], exists: _ => true, signed: _ => false);

        Assert.Equal(PlanCheckStatus.Warning, report.Items[0].Status);
        Assert.Contains("not code-signed", report.Items[0].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_missing_local_installer_fails()
    {
        var app = InstallPlannerTests.Entry("Local", AppSource.LocalFile, localPath: @"C:\apps\setup.exe");
        var report = await Run([app], exists: _ => false);

        Assert.Equal(PlanCheckStatus.Failed, report.Items[0].Status);
    }

    [Fact]
    public async Task An_empty_profile_produces_no_checks()
    {
        var report = await Run([], search: _ => []);

        Assert.Empty(report.Items);
        Assert.False(report.HasFailures);
    }

    [Fact]
    public async Task The_exported_report_says_nothing_was_installed()
    {
        var app = InstallPlannerTests.Entry("Tool", AppSource.Winget, packageId: "Vendor.Tool");
        var report = await Run([app], search: _ => [new WingetPackage("Tool", "Vendor.Tool", "1.0", "1.0")]);

        var text = report.ToText();

        Assert.Contains("CTRL+Z dry run", text);
        Assert.Contains("Nothing was installed", text);
        Assert.Contains("1 ready", text);
    }

    // ---- harness ------------------------------------------------------------------

    private static Task<DryRunReport> Run(
        IReadOnlyList<AppEntry> apps,
        Func<string, IReadOnlyList<WingetPackage>>? search = null,
        Func<string, GitHubRelease>? latest = null,
        Func<string, long?>? head = null,
        Func<string, bool>? exists = null,
        Func<string, bool?>? signed = null)
    {
        var profile = new AppProfile { Apps = apps.ToList() };
        var plan = new InstallPlanner(exists ?? (_ => false), signed ?? (_ => null)).Build(profile, "Test");

        var runner = new DryRunRunner(
            (id, _) => Task.FromResult(search?.Invoke(id) ?? (IReadOnlyList<WingetPackage>)[]),
            (repo, _) => latest is null
                ? Task.FromException<GitHubRelease>(new InvalidOperationException("no release"))
                : Task.FromResult(latest(repo)),
            (url, _) => head is null
                ? Task.FromException<long?>(new DownloadException("no head"))
                : Task.FromResult(head(url)),
            exists,
            signed);

        var byId = apps.ToDictionary(app => app.Id);
        return runner.RunAsync(plan, byId);
    }

    private static GitHubRelease Release(string tag, params GitHubAsset[] assets) => new(
        Repository: "owner/repo",
        TagName: tag,
        Title: tag,
        HtmlUrl: "https://github.com/owner/repo/releases/tag/" + tag,
        PublishedAt: DateTimeOffset.UnixEpoch,
        Assets: assets);
}
