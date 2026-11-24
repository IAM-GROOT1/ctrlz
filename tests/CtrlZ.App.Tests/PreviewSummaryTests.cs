// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.App.ViewModels;
using CtrlZ.Core.Install;
using CtrlZ.Core.Models;
using CtrlZ.Core.Safety;
using Xunit;

namespace CtrlZ.App.Tests;

/// <summary>The line above the install preview never reads as all clear while commands wait.</summary>
public class PreviewSummaryTests
{
    private static AppEntry App(string id, PostInstallCommand? command) =>
        new() { Id = id, Name = id, Source = AppSource.Winget, PackageId = "Example." + id, Enabled = true, PostInstall = command };

    private static PostInstallCommand Command(string exe, params string[] args) =>
        new() { Executable = exe, Arguments = args.ToList(), Enabled = true };

    [Fact]
    public void Commands_waiting_for_review_and_risky_commands_are_counted()
    {
        var safe = App("notes", Command(@"C:\Windows\System32\reg.exe", "import", "settings.reg"));
        var risky = App("tool", Command(@"C:\Windows\System32\netsh.exe", "advfirewall", "set", "allprofiles", "state", "off"));
        var profile = new AppProfile { Name = "Shared", Apps = [safe, risky] };
        var plan = new InstallPlanner(fileExists: _ => true, isSigned: _ => true).Build(profile, "Shared");

        var imported = new[] { safe, risky }
            .Select((app, index) => CommandPreview.ForApp(app, index + 1, ProfileOrigin.Imported, approved: false)!)
            .ToList();
        var local = new[] { safe, risky }
            .Select((app, index) => CommandPreview.ForApp(app, index + 1, ProfileOrigin.Local, approved: false)!)
            .ToList();

        Assert.Equal("2 apps in this run · 0 may need admin · 0 with warnings · 2 commands need review · 1 risky command",
            PreviewViewModel.Summary(plan, imported));
        Assert.Equal("2 apps in this run · 0 may need admin · 0 with warnings · 1 risky command",
            PreviewViewModel.Summary(plan, local));
        Assert.Equal("2 apps in this run · 0 may need admin · 0 with warnings", PreviewViewModel.Summary(plan, []));
    }
}
