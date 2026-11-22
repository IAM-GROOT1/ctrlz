// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Models;
using CtrlZ.Core.Services;

namespace CtrlZ.Core.Tests;

public class BrowserInstallHelpTests
{
    private static BrowserBackupManifest Manifest(string id, string name, BrowserFamily family = BrowserFamily.Chromium) =>
        new() { BrowserId = id, BrowserName = name, Family = family };

    [Fact]
    public void An_installed_browser_needs_no_advice() =>
        Assert.Null(BrowserInstallHelp.For(Manifest("chrome", "Google Chrome"), ["edge", "CHROME"]));

    [Fact]
    public void A_missing_known_browser_can_be_installed_with_winget()
    {
        var advice = BrowserInstallHelp.For(Manifest("brave", "Brave"), ["edge"]);

        Assert.NotNull(advice);
        Assert.True(advice.CanInstall);
        Assert.Equal("Brave.Brave", advice.WingetId);
        Assert.Contains("Open Brave once", advice.NextSteps);
        Assert.Contains("data folder", advice.NextSteps);
    }

    [Fact]
    public void Firefox_is_told_to_create_its_profile_first()
    {
        var advice = BrowserInstallHelp.For(Manifest("firefox", "Mozilla Firefox", BrowserFamily.Firefox), []);

        Assert.Equal("Mozilla.Firefox", advice!.WingetId);
        Assert.Contains("creates its profile", advice.NextSteps);
    }

    [Fact]
    public void An_unknown_browser_is_reported_without_an_install_offer()
    {
        var advice = BrowserInstallHelp.For(Manifest("whale", "Naver Whale"), ["chrome"]);

        Assert.NotNull(advice);
        Assert.False(advice.CanInstall);
        Assert.Equal("Naver Whale", advice.BrowserName);
    }

    [Fact]
    public void An_archive_without_a_browser_id_gets_no_advice() =>
        Assert.Null(BrowserInstallHelp.For(Manifest(string.Empty, "Custom"), []));

    [Theory]
    [InlineData("chrome", "Google.Chrome")]
    [InlineData("edge", "Microsoft.Edge")]
    [InlineData("vivaldi", "Vivaldi.Vivaldi")]
    [InlineData("librewolf", "LibreWolf.LibreWolf")]
    [InlineData("waterfox", "Waterfox.Waterfox")]
    [InlineData("zen", "Zen-Team.Zen-Browser")]
    public void Every_listed_id_is_also_in_the_bundled_catalog(string browser, string wingetId)
    {
        Assert.Equal(wingetId, BrowserInstallHelp.WingetIdFor(browser));
        using var stream = typeof(BrowserInstallHelp).Assembly.GetManifestResourceStream("CtrlZ.Core.Resources.winutil-applications.json")!;
        using var reader = new StreamReader(stream);
        Assert.Contains($"\"{wingetId}\"", reader.ReadToEnd());
    }
}
