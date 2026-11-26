// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.IO;
using System.Text.Json;
using CtrlZ.App.Services;
using Xunit;

namespace CtrlZ.App.Tests;

public class AppIconSourceTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CTRL+Z.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return Path.Combine([dir!, .. parts]);
    }

    [Fact]
    public void Every_bundled_logo_can_be_drawn()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(RepoFile("src", "CtrlZ.App", "Resources", "brand-icons.json")));
        var entries = json.RootElement.GetProperty("icons").GetArrayLength();

        Assert.True(entries > 80, $"only {entries} logos");
        Assert.Equal(entries, BrandIcons.Count);
    }

    [Theory]
    [InlineData("Mozilla.Firefox", "Firefox", "Firefox Browser")]
    [InlineData(null, "Mozilla Firefox", "Firefox Browser")]
    [InlineData(null, "7-Zip 24.09 (x64)", "7Zip")]
    [InlineData(null, "VLC media player", "VLC media player")]
    [InlineData("valve.steam", "Something else", "Steam")]
    public void Apps_find_their_logo_by_id_or_name(string? packageId, string name, string title)
    {
        Assert.Equal(title, BrandIcons.Find(packageId, name)?.Title);
    }

    [Theory]
    [InlineData("In-house Tool")]
    [InlineData("Microsoft Visual Studio Code")]
    [InlineData("Nilesoft Shell")]
    [InlineData("")]
    public void Apps_without_a_bundled_logo_get_none(string name)
    {
        Assert.Null(BrandIcons.Find(null, name));
    }

    [Fact]
    public void Direction_marks_around_a_name_do_not_stop_a_match()
    {
        Assert.Equal("Steam", BrandIcons.Find(null, "\u200eSteam\u200e\u200f")?.Title);
    }

    [Fact]
    public void Almost_every_catalog_app_has_a_full_colour_icon()
    {
        using var catalog = JsonDocument.Parse(File.ReadAllText(RepoFile("src", "CtrlZ.Core", "Resources", "winutil-applications.json")));
        var apps = catalog.RootElement.EnumerateObject().Select(app => app.Value).ToList();
        var withIcon = apps.Count(app =>
            CatalogIcons.Find(app.TryGetProperty("winget", out var id) ? id.GetString() : null,
                app.TryGetProperty("content", out var name) ? name.GetString() : null) is not null);

        Assert.True(CatalogIcons.Count > 220, $"only {CatalogIcons.Count} icons");
        Assert.True(withIcon >= apps.Count - 10, $"{withIcon} of {apps.Count} catalog apps have an icon");
    }

    [Fact]
    public void Bundled_icons_are_64_pixel_images()
    {
        var icon = CatalogIcons.Find("Mozilla.Firefox", null);

        Assert.NotNull(icon);
        Assert.True(icon!.IsFrozen);
        Assert.Equal(64, ((System.Windows.Media.Imaging.BitmapSource)icon).PixelWidth);
        Assert.Same(icon, CatalogIcons.Find(null, "Mozilla Firefox"));
    }

    [Theory]
    [InlineData(null, "In-house Tool")]
    [InlineData(null, "Files")]
    [InlineData(null, "Go")]
    [InlineData("Some.Unknown", "")]
    public void Unknown_or_generic_names_get_no_bundled_icon(string? packageId, string name)
    {
        Assert.Null(CatalogIcons.Find(packageId, name));
    }

    [Fact]
    public void Every_bundled_icon_says_where_it_came_from()
    {
        using var sources = JsonDocument.Parse(File.ReadAllText(RepoFile("build", "app-icons", "sources.json")));
        var withSource = sources.RootElement.EnumerateObject()
            .Count(app => app.Value.GetProperty("source").ValueKind == JsonValueKind.String);

        Assert.Equal(CatalogIcons.Count, withSource);
    }

    [Fact]
    public void An_installed_program_lends_its_icon_to_the_same_app_by_name()
    {
        InstalledIcons.Use(
        [
            ("Mozilla Firefox (x64 en-US)", @"C:\Program Files\Mozilla Firefox\firefox.exe"),
            ("Firefox Developer Edition", @"C:\Dev\firefox.exe"),
            ("7-Zip 24.09 (x64)", @"C:\Program Files\7-Zip\7zFM.exe"),
            ("No Icon App", null)
        ]);

        Assert.Equal(@"C:\Program Files\Mozilla Firefox\firefox.exe", InstalledIcons.Find("Mozilla Firefox"));
        Assert.Equal(@"C:\Program Files\Mozilla Firefox\firefox.exe", InstalledIcons.Find("Firefox"));
        Assert.Equal(@"C:\Program Files\7-Zip\7zFM.exe", InstalledIcons.Find("7-Zip"));
        Assert.Null(InstalledIcons.Find("No Icon App"));
        Assert.Null(InstalledIcons.Find("Thunderbird"));
    }
}
