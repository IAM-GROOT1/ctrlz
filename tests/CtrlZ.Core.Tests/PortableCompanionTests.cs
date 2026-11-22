// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Security.Cryptography;
using CtrlZ.Core.Updates;

namespace CtrlZ.Core.Tests;

public sealed class PortableCompanionTests : IDisposable
{
    private readonly TestDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private (string Downloaded, string Target, UpdateApplyRequest Request) Setup(string? hashOverride = null, string? companionTarget = null)
    {
        var app = _temp.Write("app/CTRL+Z.exe", "app");
        var target = _temp.Write("app/ctrlz.exe", "old cli");
        var downloaded = _temp.Write("download/ctrlz-1.4.0.exe", "new cli");
        var request = new UpdateApplyRequest
        {
            Kind = nameof(UpdateInstallKind.Portable),
            TargetExe = app,
            CompanionDownloadedFile = downloaded,
            CompanionTargetExe = companionTarget ?? target,
            CompanionExpectedSha256 = hashOverride ?? Sha(downloaded)
        };
        return (downloaded, target, request);
    }

    [Fact]
    public async Task No_companion_in_the_request_means_nothing_to_do()
    {
        Assert.Null(await PortableCompanion.PrepareAsync(new UpdateApplyRequest { TargetExe = _temp.PathOf("app", "CTRL+Z.exe") }, TestFactory.Log));
    }

    [Fact]
    public async Task No_ctrlz_exe_beside_the_app_means_nothing_to_do()
    {
        var (_, target, request) = Setup();
        File.Delete(target);

        Assert.Null(await PortableCompanion.PrepareAsync(request, TestFactory.Log));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task The_target_comes_from_the_app_folder_never_from_the_request()
    {
        var elsewhere = _temp.Write("elsewhere/victim.exe", "victim");
        var (_, target, request) = Setup(companionTarget: elsewhere);
        var companion = (await PortableCompanion.PrepareAsync(request, TestFactory.Log))!;

        companion.Swap();
        companion.Commit();

        Assert.Equal("victim", File.ReadAllText(elsewhere));
        Assert.Equal("new cli", File.ReadAllText(target));
    }

    [Fact]
    public async Task A_wrong_hash_stops_and_leaves_ctrlz_exe_and_no_staged_copy()
    {
        var (_, target, request) = Setup(new string('0', 64));

        await Assert.ThrowsAsync<UpdateVerificationException>(() => PortableCompanion.PrepareAsync(request, TestFactory.Log));

        Assert.Equal("old cli", File.ReadAllText(target));
        Assert.False(File.Exists(target + ".new"));
    }

    [Fact]
    public async Task A_missing_hash_is_refused()
    {
        var (_, target, request) = Setup();
        request.CompanionExpectedSha256 = null;

        await Assert.ThrowsAsync<UpdateVerificationException>(() => PortableCompanion.PrepareAsync(request, TestFactory.Log));
        Assert.Equal("old cli", File.ReadAllText(target));
    }

    [Fact]
    public async Task A_missing_download_only_skips_the_command_line()
    {
        var (downloaded, target, request) = Setup();
        File.Delete(downloaded);

        Assert.Null(await PortableCompanion.PrepareAsync(request, TestFactory.Log));
        Assert.Equal("old cli", File.ReadAllText(target));
    }

    [Fact]
    public async Task The_hashed_bytes_are_the_bytes_moved_into_place()
    {
        var (downloaded, target, request) = Setup();
        var companion = (await PortableCompanion.PrepareAsync(request, TestFactory.Log))!;

        // The download folder copy is gone: it was moved next to the app before hashing.
        Assert.False(File.Exists(downloaded));
        File.WriteAllText(downloaded, "swapped in later");

        companion.Swap();
        Assert.Equal("new cli", File.ReadAllText(target));
    }

    [Fact]
    public async Task Swap_then_commit_leaves_only_the_new_command_line()
    {
        var (_, target, request) = Setup();
        var companion = (await PortableCompanion.PrepareAsync(request, TestFactory.Log))!;

        Assert.True(companion.Swap());
        companion.Commit();

        Assert.Equal("new cli", File.ReadAllText(target));
        Assert.False(File.Exists(target + ".old"));
        Assert.False(File.Exists(target + ".new"));
    }

    [Fact]
    public async Task Swap_then_restore_brings_the_old_command_line_back()
    {
        var (_, target, request) = Setup();
        var companion = (await PortableCompanion.PrepareAsync(request, TestFactory.Log))!;

        companion.Swap();
        companion.Restore();

        Assert.Equal("old cli", File.ReadAllText(target));
        Assert.False(File.Exists(target + ".old"));
    }

    [Fact]
    public async Task Restore_without_a_swap_cleans_up_the_staged_copy_only()
    {
        var (_, target, request) = Setup();
        var companion = (await PortableCompanion.PrepareAsync(request, TestFactory.Log))!;

        companion.Restore();

        Assert.Equal("old cli", File.ReadAllText(target));
        Assert.False(File.Exists(target + ".new"));
    }

    [Fact]
    public async Task A_locked_command_line_is_kept_and_the_swap_reports_false()
    {
        var (_, target, request) = Setup();
        var companion = (await PortableCompanion.PrepareAsync(request, TestFactory.Log))!;

        bool swapped;
        using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            swapped = companion.Swap();
        }

        Assert.False(swapped);
        Assert.Equal("old cli", File.ReadAllText(target));
        Assert.False(File.Exists(target + ".new"));
    }

    [Fact]
    public void The_command_line_asset_of_the_exact_release_is_selected_with_the_same_checks()
    {
        SemanticVersion.TryParse("1.4.0", out var version);
        static UpdateAsset Asset(string name, string host = "github.com") =>
            new(name, $"https://{host}/IAM-GROOT1/ctrlz/releases/download/v1.4.0/{name}", 100, null);
        var release = new ReleaseInfo("v1.4.0", version!, "1.4.0", null, "https://github.com", null, false,
            [Asset("CTRL+Z-Portable-1.4.0.exe"), Asset("ctrlz-1.4.0.exe")]);

        Assert.True(UpdateAssetSelector.TrySelectCli(release, out var selection, out _));
        Assert.Equal("ctrlz-1.4.0.exe", selection!.Asset.Name);

        var older = release with { Assets = [Asset("ctrlz-1.3.9.exe")] };
        Assert.False(UpdateAssetSelector.TrySelectCli(older, out _, out _));

        var foreign = release with { Assets = [Asset("ctrlz-1.4.0.exe", "example.com")] };
        Assert.False(UpdateAssetSelector.TrySelectCli(foreign, out _, out _));
    }
}
