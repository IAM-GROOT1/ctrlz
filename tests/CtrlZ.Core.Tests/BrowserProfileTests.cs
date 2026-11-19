// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Models;
using CtrlZ.Core.Services;

namespace CtrlZ.Core.Tests;

public class ChromiumProfileTests
{
    private static string WriteProfile(TestDirectory temp, string userDataRelative, string dir, string name)
    {
        var root = temp.PathOf(userDataRelative.Split('/'));
        Directory.CreateDirectory(Path.Combine(root, dir));
        File.WriteAllText(Path.Combine(root, dir, "Preferences"), "{}");
        return root;
    }

    private static string WriteLocalState(string root, params (string Dir, string Name)[] profiles)
    {
        var info = string.Join(",", profiles.Select(p =>
            $"\"{p.Dir}\":{{\"name\":\"{p.Name}\",\"user_name\":\"user@example.com\"}}"));
        var path = Path.Combine(root, "Local State");
        File.WriteAllText(path, $"{{\"profile\":{{\"info_cache\":{{{info}}}}},\"browser\":{{\"enabled_labs_experiments\":[\"one\"]}}}}");
        return path;
    }

    [Fact]
    public void Reads_display_names_from_local_state_in_profile_order()
    {
        using var temp = new TestDirectory();
        var root = temp.CreateDirectory("User Data");
        WriteProfile(temp, "User Data", "Default", "Work profile");
        WriteProfile(temp, "User Data", "Profile 1", "Gaming");
        WriteLocalState(root, ("Default", "Work profile"), ("Profile 1", "Gaming"));

        var profiles = ChromiumProfileReader.ReadProfiles(root);

        Assert.Equal(2, profiles.Count);
        Assert.Equal("Work profile", profiles[0].DisplayName);
        Assert.True(profiles[0].IsDefault);
        Assert.Equal("Gaming", profiles[1].DisplayName);
        Assert.Equal("Profile 1", profiles[1].DirectoryName);
    }

    [Fact]
    public void Falls_back_to_directory_names_when_local_state_has_no_info_cache()
    {
        using var temp = new TestDirectory();
        var root = temp.CreateDirectory("User Data");
        WriteProfile(temp, "User Data", "Default", "Default");
        WriteProfile(temp, "User Data", "Profile 2", "Profile 2");
        File.WriteAllText(Path.Combine(root, "Local State"), "{}");

        var profiles = ChromiumProfileReader.ReadProfiles(root);

        Assert.Equal(2, profiles.Count);
        Assert.Equal("Default", profiles[0].DisplayName);
        Assert.Equal("Profile 2", profiles[1].DisplayName);
    }

    [Fact]
    public void Supports_flat_single_profile_layout()
    {
        using var temp = new TestDirectory();
        var root = temp.CreateDirectory("Opera Stable");
        File.WriteAllText(Path.Combine(root, "Preferences"), "{}");
        WriteLocalState(root, ("Default", "Opera profile"));

        var profiles = ChromiumProfileReader.ReadProfiles(root, flatLayout: true);

        var profile = Assert.Single(profiles);
        Assert.True(profile.IsFlat);
        Assert.Equal(".", profile.DirectoryName);
        Assert.Equal("Opera profile", profile.DisplayName);
    }

    [Fact]
    public void Validates_user_data_folder_requires_local_state()
    {
        using var temp = new TestDirectory();
        var root = temp.CreateDirectory("User Data");
        Directory.CreateDirectory(Path.Combine(root, "Default"));

        Assert.False(ChromiumProfileReader.ValidateUserDataFolder(root, out var error));
        Assert.Contains("Local State", error);

        File.WriteAllText(Path.Combine(root, "Local State"), "{\"profile\":{}}");
        Assert.True(ChromiumProfileReader.ValidateUserDataFolder(root, out _));
    }

    [Fact]
    public void Custom_detector_rejects_application_folder()
    {
        using var temp = new TestDirectory();
        var appFolder = temp.CreateDirectory("Application");
        File.WriteAllText(Path.Combine(appFolder, "chrome.exe"), string.Empty);
        var detector = new BrowserDetector(TestFactory.Log);

        Assert.Throws<BrowserPathValidationException>(() => detector.CreateCustom(appFolder));
    }

    [Fact]
    public void Local_state_sanitizer_accepts_a_utf8_bom()
    {
        var sanitized = BrowserLocalStateSanitizer.Sanitize("\uFEFF{\"browser\":{\"x\":1},\"os_crypt\":{\"encrypted_key\":\"secret\"}}");
        var json = sanitized.ToJsonString();
        Assert.Contains("\"x\":1", json);
        Assert.DoesNotContain("secret", json);
    }

    [Fact]
    public void Local_state_sanitizer_removes_os_crypt_but_keeps_flags()
    {
        var state = "{\"os_crypt\":{\"encrypted_key\":\"secret\"},\"browser\":{\"enabled_labs_experiments\":[\"x\"]},\"profile\":{\"info_cache\":{\"Default\":{\"name\":\"Work\"}}}}";
        var sanitized = BrowserLocalStateSanitizer.Sanitize(state);
        var json = sanitized.ToJsonString();

        Assert.DoesNotContain("os_crypt", json);
        Assert.DoesNotContain("secret", json);
        Assert.Contains("enabled_labs_experiments", json);
        Assert.Contains("Work", json);
    }
}
