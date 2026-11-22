// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Install;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Tests;

public class AppEntryCloneTests
{
    private static AppEntry Original() => new()
    {
        Id = "a",
        Name = "Tool",
        AfterAppIds = ["b"],
        IsAutoDependency = true,
        PostInstall = new PostInstallCommand { Executable = "reg.exe", Arguments = ["import", "a.reg"], Enabled = true }
    };

    [Fact]
    public void Clone_keeps_command_and_dependency_fields()
    {
        var original = Original();
        var copy = original.Clone();

        Assert.Equal(new[] { "b" }, copy.AfterAppIds);
        Assert.True(copy.IsAutoDependency);
        Assert.NotNull(copy.PostInstall);
        Assert.Equal(PostInstallApprovalStore.HashOf(original.PostInstall!), PostInstallApprovalStore.HashOf(copy.PostInstall!));
    }

    [Fact]
    public void Clone_is_deep_so_editing_the_copy_never_changes_the_original()
    {
        var original = Original();
        var copy = original.Clone();

        copy.PostInstall!.Arguments.Add("--changed");
        copy.AfterAppIds.Add("c");

        Assert.Equal(2, original.PostInstall!.Arguments.Count);
        Assert.Single(original.AfterAppIds);
    }

    [Fact]
    public void Profile_snapshot_used_by_the_install_engine_keeps_commands()
    {
        var profile = new AppProfile { Apps = [Original()] };

        Assert.NotNull(profile.Clone().Apps[0].PostInstall);
    }
}
