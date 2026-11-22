// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Text.Json;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Models;
using CtrlZ.Core.Services;

namespace CtrlZ.Core.Tests;

public sealed class PathPrivacyTests : IDisposable
{
    private readonly TestDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static PathPrivacy Privacy() => new(
        [
            ("%LOCALAPPDATA%", @"C:\Users\jdoe\AppData\Local"),
            ("%APPDATA%", @"C:\Users\jdoe\AppData\Roaming"),
            ("%TEMP%", @"C:\Users\jdoe\AppData\Local\Temp\"),
            ("%USERPROFILE%", @"C:\Users\jdoe")
        ],
        "jdoe",
        "DESKTOP-ABC123");

    [Theory]
    [InlineData(@"C:\Users\jdoe\AppData\Local\CTRL+Z\logs\a.log", @"%LOCALAPPDATA%\CTRL+Z\logs\a.log")]
    [InlineData(@"C:\Users\jdoe\AppData\Local\Temp\x.tmp", @"%TEMP%\x.tmp")]
    [InlineData(@"c:\users\JDOE\Downloads\setup.exe", @"%USERPROFILE%\Downloads\setup.exe")]
    [InlineData(@"D:\Games\Steam", @"D:\Games\Steam")]
    public void Personal_folders_become_tokens_longest_first(string path, string expected)
    {
        Assert.Equal(expected, Privacy().Tokenize(path));
    }

    [Fact]
    public void Tokens_expand_back_losslessly_in_plain_and_json_text()
    {
        var privacy = Privacy();
        const string path = @"C:\Users\jdoe\Downloads\setup.exe";
        var json = JsonSerializer.Serialize(new { path });

        var tokenized = privacy.Tokenize(json);

        Assert.DoesNotContain("jdoe", tokenized, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(path, JsonSerializer.Deserialize<Dictionary<string, string>>(privacy.Expand(tokenized, json: true))!["path"]);
        Assert.Equal(path, privacy.Expand(privacy.Tokenize(path)));
    }

    [Fact]
    public void Log_lines_hide_the_account_and_pc_names_as_whole_words()
    {
        var line = Privacy().ForLog(@"Saved to C:\Users\jdoe\Documents\p.json on DESKTOP-ABC123 by jdoe; jdoensen stays");

        Assert.Equal(@"Saved to %USERPROFILE%\Documents\p.json on <pc> by <user>; jdoensen stays", line);
    }

    [Fact]
    public void The_file_log_never_writes_the_account_path()
    {
        var logs = _temp.PathOf("logs");
        using (var log = new FileAppLog(logs))
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            log.Write(LogLevel.Info, $"Data folder: {Path.Combine(profile, "AppData", "Local", "CTRL+Z")}",
                new IOException($"Could not open {Path.Combine(profile, "secret.txt")}"));
        }

        var text = string.Join("\n", Directory.GetFiles(logs).Select(File.ReadAllText));
        Assert.DoesNotContain(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%LOCALAPPDATA%", text);
    }

    [Fact]
    public void Install_state_on_disk_holds_tokens_and_loads_back_real_paths()
    {
        var paths = new AppPaths(_temp.PathOf("data"));
        paths.EnsureCreated();
        var store = new InstallStateStore(paths.InstallStateFile, TestFactory.Log);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Path.Combine(profile, "Downloads", "setup.exe");
        var state = new InstallState
        {
            LogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CTRL+Z", "logs", "i.log"),
            ProfileSnapshot = new AppProfile { Apps = [new AppEntry { Id = "a", Name = "A", LocalPath = local }] }
        };

        store.Save(state);

        var onDisk = File.ReadAllText(paths.InstallStateFile);
        Assert.DoesNotContain(profile.Replace("\\", "\\\\"), onDisk, StringComparison.OrdinalIgnoreCase);
        var loaded = store.Load()!;
        Assert.Equal(state.LogPath, loaded.LogPath);
        Assert.Equal(local, loaded.ProfileSnapshot!.Apps[0].LocalPath);
    }
}
