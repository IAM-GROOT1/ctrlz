// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Install;
using CtrlZ.Core.Models;
using CtrlZ.Core.Safety;

namespace CtrlZ.Core.Tests;

public class CommandPreviewPlanTests
{
    private static AppEntry App(string id, params string[] after) => new()
    {
        Id = id,
        Name = id,
        Source = AppSource.LocalFile,
        LocalPath = "x.exe",
        AfterAppIds = after.ToList(),
        PostInstall = new PostInstallCommand { Executable = "reg.exe", Arguments = ["import", id + ".reg"], Enabled = true }
    };

    [Fact]
    public void Command_lines_follow_the_dependency_order_and_numbers_of_the_plan()
    {
        var profile = new AppProfile { Apps = [App("app", "runtime"), App("runtime")] };
        var plan = new InstallPlanner(_ => true, _ => true).Build(profile, "P");

        var lines = CommandPreview.ForPlan(plan, profile, ProfileOrigin.Local, _ => false);

        Assert.Equal(new[] { "runtime", "app" }, lines.Select(line => line.AppId));
        Assert.Equal(plan.Items.Select(item => item.Order), lines.Select(line => line.Order));
    }

    [Fact]
    public void Apps_the_plan_will_not_run_have_no_command_line()
    {
        var profile = new AppProfile { Apps = [App("a", "b"), App("b", "a"), App("c")] };
        var plan = new InstallPlanner(_ => true, _ => true).Build(profile, "P");

        var lines = CommandPreview.ForPlan(plan, profile, ProfileOrigin.Local, _ => false);

        Assert.Equal(new[] { "c" }, lines.Select(line => line.AppId));
        Assert.Contains(plan.PlanWarnings, warning => warning.Contains("loop", StringComparison.OrdinalIgnoreCase));
    }
}
