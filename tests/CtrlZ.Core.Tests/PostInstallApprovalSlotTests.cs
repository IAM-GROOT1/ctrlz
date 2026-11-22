// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Text.Json;
using CtrlZ.Core.Install;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Tests;

public sealed class PostInstallApprovalSlotTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ctrlz-slots-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private string StorePath => Path.Combine(_folder, "approvals.json");

    private static PostInstallCommand Command(string arg = "a.reg", string? id = null) =>
        new() { Id = id, Executable = "reg.exe", Arguments = ["import", arg], Enabled = true };

    [Fact]
    public void An_approval_belongs_to_one_app_slot_only()
    {
        var store = new PostInstallApprovalStore(StorePath);
        var command = Command();

        store.Approve("p1", "appA", command);

        Assert.Equal(ApprovalState.Current, store.StateOf("p1", "appA", command));
        Assert.Equal(ApprovalState.None, store.StateOf("p1", "appB", command.Clone()));
        Assert.Equal(ApprovalState.None, store.StateOf("p2", "appA", command));
    }

    [Fact]
    public void Editing_an_approved_command_makes_its_slot_outdated_and_reapproving_fixes_it()
    {
        var store = new PostInstallApprovalStore(StorePath);
        var command = Command(id: "c1");
        store.Approve("p1", "app", command);

        var edited = Command("other.reg", id: "c1");

        Assert.Equal(ApprovalState.Outdated, store.StateOf("p1", "app", edited));
        store.Approve("p1", "app", edited);
        Assert.Equal(ApprovalState.Current, store.StateOf("p1", "app", edited));
        Assert.Equal(ApprovalState.Outdated, store.StateOf("p1", "app", command));
    }

    [Fact]
    public void A_different_command_id_in_the_same_app_is_a_different_slot()
    {
        var store = new PostInstallApprovalStore(StorePath);
        store.Approve("p1", "app", Command(id: "c1"));

        Assert.Equal(ApprovalState.None, store.StateOf("p1", "app", Command("x.reg", id: "c2")));
    }

    [Fact]
    public void Approvals_survive_a_reload()
    {
        var command = Command(id: "c1");
        new PostInstallApprovalStore(StorePath).Approve("p1", "app", command);

        Assert.True(new PostInstallApprovalStore(StorePath).IsApproved("p1", "app", command));
    }

    [Fact]
    public void Version_1_records_stay_valid_for_their_exact_text_until_it_changes()
    {
        Directory.CreateDirectory(_folder);
        var command = Command();
        var v1 = new Dictionary<string, List<string>> { ["p1"] = [PostInstallApprovalStore.HashOf(command)] };
        File.WriteAllText(StorePath, JsonSerializer.Serialize(v1));

        var store = new PostInstallApprovalStore(StorePath);

        Assert.Equal(ApprovalState.Current, store.StateOf("p1", "app", command));
        // A v1 record cannot say which slot it was for, so an edit reads as never approved: blocked.
        Assert.Equal(ApprovalState.None, store.StateOf("p1", "app", Command("changed.reg")));
    }

    [Fact]
    public void Migration_keeps_version_1_records_and_writes_version_2_on_the_next_approval()
    {
        Directory.CreateDirectory(_folder);
        var old = Command("old.reg");
        File.WriteAllText(StorePath, JsonSerializer.Serialize(
            new Dictionary<string, List<string>> { ["p1"] = [PostInstallApprovalStore.HashOf(old)] }));

        var fresh = Command("new.reg", id: "c9");
        new PostInstallApprovalStore(StorePath).Approve("p1", "other", fresh);

        using var document = JsonDocument.Parse(File.ReadAllText(StorePath));
        Assert.Equal(2, document.RootElement.GetProperty("version").GetInt32());
        var reloaded = new PostInstallApprovalStore(StorePath);
        Assert.True(reloaded.IsApproved("p1", "app", old));
        Assert.True(reloaded.IsApproved("p1", "other", fresh));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"version\": 2, \"slots\": 5}")]
    public void An_unreadable_record_approves_nothing(string content)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(StorePath, content);

        Assert.Equal(ApprovalState.None, new PostInstallApprovalStore(StorePath).StateOf("p1", "app", Command()));
    }

    [Fact]
    public void Forget_removes_slot_and_version_1_approvals_of_a_profile()
    {
        Directory.CreateDirectory(_folder);
        var old = Command("old.reg");
        File.WriteAllText(StorePath, JsonSerializer.Serialize(
            new Dictionary<string, List<string>> { ["p1"] = [PostInstallApprovalStore.HashOf(old)] }));
        var store = new PostInstallApprovalStore(StorePath);
        store.Approve("p1", "app", Command(id: "c1"));

        store.Forget("p1");

        var reloaded = new PostInstallApprovalStore(StorePath);
        Assert.Equal(ApprovalState.None, reloaded.StateOf("p1", "app", Command(id: "c1")));
        Assert.Equal(ApprovalState.None, reloaded.StateOf("p1", "app", old));
    }

    [Theory]
    [InlineData("a|b", "c", "a", "b|c")]
    [InlineData("a/1:b", "c", "a", "1:b/1:c")]
    [InlineData("app", "default", "app", null)]
    public void Ids_chosen_by_a_profile_file_cannot_make_two_slots_share_a_key(
        string appA, string? commandA, string appB, string? commandB)
    {
        var keyA = PostInstallApprovalStore.SlotKey(appA, Command(id: commandA));
        var keyB = PostInstallApprovalStore.SlotKey(appB, Command(id: commandB));

        // "default" written explicitly is the same slot as a command without an id, on purpose.
        if (commandA == "default" && commandB is null && appA == appB)
        {
            Assert.Equal(keyA, keyB);
            return;
        }

        Assert.NotEqual(keyA, keyB);
    }

    [Fact]
    public void The_slot_id_is_not_part_of_the_command_hash()
    {
        Assert.Equal(PostInstallApprovalStore.HashOf(Command(id: "a")), PostInstallApprovalStore.HashOf(Command(id: "b")));
    }

    [Fact]
    public void A_profile_without_command_ids_keeps_its_content_hash()
    {
        // Commands saved before ids existed have no "id" in JSON, so a Local profile stays Local.
        // ContentHash serializes with JsonDefaults.Profile; the command object must have no "id" key.
        var profile = new AppProfile { Id = "p", Apps = [new AppEntry { Id = "a", PostInstall = Command() }] };
        var json = JsonSerializer.Serialize(profile, CtrlZ.Core.Infrastructure.JsonDefaults.Profile);
        using var document = JsonDocument.Parse(json);
        var app = document.RootElement.EnumerateObject()
            .First(property => property.Name.Equals("apps", StringComparison.OrdinalIgnoreCase))
            .Value[0];
        var command = app.EnumerateObject()
            .First(property => property.Name.Equals("postInstall", StringComparison.OrdinalIgnoreCase))
            .Value;

        Assert.DoesNotContain(command.EnumerateObject(), property => property.Name.Equals("id", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("{\"version\": 1, \"slots\": {}}")]
    [InlineData("{\"version\": 99, \"slots\": {}}")]
    [InlineData("{\"version\": \"2\"}")]
    public void A_record_with_an_unknown_version_approves_nothing(string content)
    {
        Directory.CreateDirectory(_folder);
        var command = Command();
        var withSlot = content.Replace("{}", "{\"p1\": {\"" + PostInstallApprovalStore.SlotKey("app", command).Replace("\\", "\\\\") + "\": \"" + PostInstallApprovalStore.HashOf(command) + "\"}}");
        File.WriteAllText(StorePath, withSlot);

        Assert.Equal(ApprovalState.None, new PostInstallApprovalStore(StorePath).StateOf("p1", "app", command));
    }

    [Theory]
    [InlineData("{\"p1\": null}")]
    [InlineData("{\"version\": 2, \"legacy\": {\"p1\": null}, \"slots\": {\"p1\": null}}")]
    public void Null_lists_load_safely_and_approve_nothing(string content)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(StorePath, content);

        Assert.Equal(ApprovalState.None, new PostInstallApprovalStore(StorePath).StateOf("p1", "app", Command()));
    }

    [Fact]
    public void A_slot_record_overrides_an_old_version_1_record_for_that_slot()
    {
        Directory.CreateDirectory(_folder);
        var original = Command("a.reg", id: "c1");
        File.WriteAllText(StorePath, JsonSerializer.Serialize(
            new Dictionary<string, List<string>> { ["p1"] = [PostInstallApprovalStore.HashOf(original)] }));
        var store = new PostInstallApprovalStore(StorePath);
        var edited = Command("b.reg", id: "c1");
        store.Approve("p1", "app", edited);

        // Editing back to the v1-approved text is a change since the last approval of this slot.
        Assert.Equal(ApprovalState.Outdated, store.StateOf("p1", "app", original));
    }

    [Fact]
    public void The_editor_keeps_the_slot_id_of_an_edited_command_and_gives_a_new_command_one()
    {
        var input = PostInstallCommandForm.From(Command());

        var edited = PostInstallCommandForm.Parse(input, existingId: "keep-me").Command!;
        var created = PostInstallCommandForm.Parse(input).Command!;

        Assert.Equal("keep-me", edited.Id);
        Assert.False(string.IsNullOrWhiteSpace(created.Id));
    }
}
