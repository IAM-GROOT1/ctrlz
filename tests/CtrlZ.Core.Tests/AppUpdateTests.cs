// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.AppUpdates;

namespace CtrlZ.Core.Tests;

public sealed class AppUpdateTests : IDisposable
{
    private readonly TestDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static string Row(string name, string id, string version, string available, string source = "winget") =>
        name.PadRight(30) + id.PadRight(36) + version.PadRight(14) + available.PadRight(14) + source;

    private static string Table(params string[] rows)
    {
        var header = "Name".PadRight(30) + "Id".PadRight(36) + "Version".PadRight(14) + "Available".PadRight(14) + "Source";
        return string.Join(Environment.NewLine, new[] { header, new string('-', header.Length) }.Concat(rows));
    }

    private static string UpgradeOutput() => string.Join(Environment.NewLine,
        Table(
            Row("Mozilla Firefox", "Mozilla.Firefox", "121.0", "122.0"),
            Row("7-Zip", "7zip.7zip", "23.01", "24.08")),
        "2 upgrades available.",
        string.Empty,
        "The following packages have an upgrade available, but require explicit targeting for upgrade:",
        Table(Row("Discord", "Discord.Discord", "1.0.9", "1.0.10")),
        "1 package(s) have version numbers that cannot be determined. Use --include-unknown to see all results.");

    private static AppUpdate Update(string id = "Mozilla.Firefox", string available = "122.0", bool pinned = false) =>
        new(id, id, "1.0", available, "winget", pinned);

    [Fact]
    public void Upgrade_output_lists_updates_and_marks_pinned_ones()
    {
        var updates = AppUpdateParser.Parse(UpgradeOutput());

        Assert.Equal(new[] { "Mozilla.Firefox", "7zip.7zip", "Discord.Discord" }, updates.Select(u => u.Id));
        Assert.Equal(new[] { false, false, true }, updates.Select(u => u.RequiresExplicit));
        Assert.Equal("122.0", updates[0].AvailableVersion);
        Assert.Equal("121.0", updates[0].InstalledVersion);
    }

    [Theory]
    [InlineData("")]
    [InlineData("No installed package found matching input criteria.")]
    [InlineData("No available upgrade found.")]
    public void Nothing_to_update_gives_an_empty_list(string output)
    {
        Assert.Empty(AppUpdateParser.Parse(output));
    }

    [Theory]
    [InlineData(0, AppUpdateOutcomeKind.Updated)]
    [InlineData(unchecked((int)0x8A150109), AppUpdateOutcomeKind.UpdatedRestartNeeded)]
    [InlineData(unchecked((int)0x8A15010A), AppUpdateOutcomeKind.UpdatedRestartNeeded)]
    [InlineData(unchecked((int)0x8A15002B), AppUpdateOutcomeKind.AlreadyCurrent)]
    [InlineData(1603, AppUpdateOutcomeKind.Failed)]
    public void Exit_codes_map_to_outcomes(int exitCode, AppUpdateOutcomeKind expected)
    {
        Assert.Equal(expected, AppUpdateParser.Outcome(Update(), exitCode, null).Kind);
    }

    [Fact]
    public void Ignoring_hides_only_that_version_and_survives_a_reload()
    {
        var path = _temp.PathOf("state", AppUpdateIgnoreStore.FileName);
        new AppUpdateIgnoreStore(path).Ignore(Update(available: "122.0"));

        var reloaded = new AppUpdateIgnoreStore(path);

        Assert.True(reloaded.IsIgnored(Update(available: "122.0")));
        Assert.False(reloaded.IsIgnored(Update(available: "123.0")));
        reloaded.Unignore("Mozilla.Firefox");
        Assert.False(new AppUpdateIgnoreStore(path).IsIgnored(Update(available: "122.0")));
    }

    [Fact]
    public void An_unreadable_ignore_file_ignores_nothing()
    {
        var path = _temp.Write("state/" + AppUpdateIgnoreStore.FileName, "{ not json");

        Assert.False(new AppUpdateIgnoreStore(path).IsIgnored(Update()));
    }

    [Fact]
    public void Update_all_skips_pinned_and_ignored_apps()
    {
        var ignores = new AppUpdateIgnoreStore(_temp.PathOf("state", AppUpdateIgnoreStore.FileName));
        var ignored = Update("Ignored.App");
        ignores.Ignore(ignored);

        var chosen = AppUpdateRunner.ForUpdateAll([Update("A.App"), Update("Pinned.App", pinned: true), ignored], ignores);

        Assert.Equal(new[] { "A.App" }, chosen.Select(u => u.Id));
    }

    [Fact]
    public async Task The_runner_updates_in_order_reports_each_and_stops_on_cancel()
    {
        using var cts = new CancellationTokenSource();
        var calls = new List<string>();
        var runner = new AppUpdateRunner((id, _, _) =>
        {
            calls.Add(id);
            if (id == "B.App")
            {
                cts.Cancel();
                return Task.FromResult((unchecked((int)0x8A15002B), string.Empty));
            }

            return Task.FromResult((0, string.Empty));
        });

        var outcomes = await runner.RunAsync([Update("A.App"), Update("B.App"), Update("C.App")], null, null, cts.Token);

        Assert.Equal(new[] { "A.App", "B.App" }, calls);
        Assert.Equal(
            new[] { AppUpdateOutcomeKind.Updated, AppUpdateOutcomeKind.AlreadyCurrent, AppUpdateOutcomeKind.Cancelled },
            outcomes.Select(o => o.Kind));
    }

    [Fact]
    public async Task A_winget_that_cannot_start_is_a_failure_not_a_crash()
    {
        var runner = new AppUpdateRunner((_, _, _) => throw new InvalidOperationException("winget missing"));

        var outcomes = await runner.RunAsync([Update()], null, null, CancellationToken.None);

        Assert.Equal(AppUpdateOutcomeKind.Failed, Assert.Single(outcomes).Kind);
    }
}
