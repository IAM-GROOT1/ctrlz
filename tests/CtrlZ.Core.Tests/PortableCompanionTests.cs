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

    private (string Downloaded, string Target, UpdateApplyRequest Request) Setup(string? hashOverride = null)
    {
        var target = _temp.Write("app/ctrlz.exe", "old cli");
        var downloaded = _temp.Write("download/ctrlz-1.4.0.exe", "new cli");
        var request = new UpdateApplyRequest
        {
            CompanionDownloadedFile = downloaded,
            CompanionTargetExe = target,
            CompanionExpectedSha256 = hashOverride ?? Sha(downloaded)
        };
        return (downloaded, target, request);
    }

    [Fact]
    public async Task No_companion_in_the_request_means_nothing_to_do()
    {
        Assert.Null(await PortableCompanion.PrepareAsync(new UpdateApplyRequest(), TestFactory.Log));
    }

    [Fact]
    public async Task A_wrong_hash_stops_before_anything_on_disk_changes()
    {
        var (downloaded, target, request) = Setup(new string('0', 64));

        await Assert.ThrowsAsync<UpdateVerificationException>(() => PortableCompanion.PrepareAsync(request, TestFactory.Log));

        Assert.Equal("old cli", File.ReadAllText(target));
        Assert.True(File.Exists(downloaded));
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
    public async Task Swap_then_commit_leaves_only_the_new_command_line()
    {
        var (_, target, request) = Setup();
        var companion = (await PortableCompanion.PrepareAsync(request, TestFactory.Log))!;

        Assert.True(companion.Swap());
        companion.Commit();

        Assert.Equal("new cli", File.ReadAllText(target));
        Assert.False(File.Exists(target + ".old"));
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
    public async Task A_running_command_line_is_left_alone_and_the_swap_reports_false()
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
        companion.Restore(); // nothing was swapped, so nothing changes
        Assert.Equal("old cli", File.ReadAllText(target));
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
