// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Models;
using CtrlZ.Core.Services;

namespace CtrlZ.Core.Tests;

public class FirefoxProfileTests
{
    private static string CreateRoot(TestDirectory temp, string name = "Firefox")
    {
        var root = temp.CreateDirectory(name);
        temp.Write($"{name}/profiles.ini",
            "[Profile0]\nName=Work\nIsRelative=1\nPath=Profiles/abc.default-release\nDefault=1\n\n" +
            "[Profile1]\nName=Play\nIsRelative=1\nPath=Profiles/xyz.play\n\n" +
            "[Install4F96D1932A9F858E]\nDefault=Profiles/abc.default-release\nLocked=1\n");
        temp.Write($"{name}/Profiles/abc.default-release/prefs.js", "user_pref(\"x\", 1);");
        temp.Write($"{name}/Profiles/xyz.play/prefs.js", "user_pref(\"x\", 2);");
        return root;
    }

    [Fact]
    public void Reads_names_and_paths_from_profiles_ini()
    {
        using var temp = new TestDirectory();
        var root = CreateRoot(temp);

        var profiles = FirefoxProfileReader.ReadProfiles(root);

        Assert.Equal(2, profiles.Count);
        Assert.Equal("Work", profiles[0].DisplayName);
        Assert.True(profiles[0].IsDefault);
        Assert.Equal("Profiles/abc.default-release", profiles[0].DirectoryName);
        Assert.Equal("Play", profiles[1].DisplayName);
        Assert.Equal("Profiles/xyz.play", profiles[1].DirectoryName);
    }

    [Fact]
    public void Validates_a_profiles_root()
    {
        using var temp = new TestDirectory();
        var root = CreateRoot(temp);

        Assert.True(FirefoxProfileReader.ValidateProfileRoot(root, out _));
        Assert.False(FirefoxProfileReader.ValidateProfileRoot(temp.CreateDirectory("empty"), out var error));
        Assert.Contains("profiles", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Supports_single_profile_layouts()
    {
        using var temp = new TestDirectory();
        var root = temp.CreateDirectory("Tor Data");
        temp.Write("Tor Data/profile.default/prefs.js", "user_pref(\"x\", 1);");
        temp.Write("Tor Data/profile.default/places.sqlite", "fake");

        var profiles = FirefoxProfileReader.ReadProfiles(root);

        var profile = Assert.Single(profiles);
        Assert.Equal(".", profile.DirectoryName);
        Assert.Equal(Path.Combine(root, "profile.default"), profile.DirectoryPath);
        Assert.True(profile.IsDefault);
    }

    [Fact]
    public void Custom_detector_identifies_firefox_family()
    {
        using var temp = new TestDirectory();
        var root = CreateRoot(temp);
        var detector = new BrowserDetector(TestFactory.Log);

        var browser = detector.CreateCustom(root);

        Assert.Equal(BrowserFamily.Firefox, browser.Family);
        Assert.Equal(2, browser.Profiles.Count);
        Assert.Contains("Firefox", browser.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Profiles_ini_add_or_update_preserves_other_sections()
    {
        using var temp = new TestDirectory();
        var root = CreateRoot(temp);

        var ini = FirefoxProfilesIni.Load(root);
        ini.AddOrUpdate("Restored", "Profiles/Profile 1");
        ini.Save();

        var saved = File.ReadAllText(Path.Combine(root, "profiles.ini"));
        Assert.Contains("[Profile0]", saved);
        Assert.Contains("[Install4F96D1932A9F858E]", saved);
        Assert.Contains("Name=Restored", saved);
        Assert.Contains("Path=Profiles/Profile 1", saved);
    }
}
