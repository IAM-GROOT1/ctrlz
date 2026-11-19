// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Models;
using CtrlZ.Core.Updates;

namespace CtrlZ.Core.Tests;

public class UpdateCheckTests
{
    private static readonly SemanticVersion Current = SemanticVersion.Parse("1.1.3");

    // ---- Release selection ---------------------------------------------------------

    [Fact]
    public async Task Stable_channel_ignores_pre_releases()
    {
        var json = Json(
            Rel("v1.3.0-beta.1", pre: true),
            Rel("v1.2.0"));

        var check = await CreateService(json).CheckAsync(Current, UpdateChannel.Stable);

        Assert.True(check.UpdateAvailable);
        Assert.Equal("1.2.0", check.Latest!.Version.ToString());
    }

    [Fact]
    public async Task Pre_release_channel_takes_the_newer_pre_release()
    {
        var json = Json(
            Rel("v1.2.0"),
            Rel("v1.3.0-beta.1", pre: true));

        var check = await CreateService(json).CheckAsync(Current, UpdateChannel.PreRelease);

        Assert.True(check.UpdateAvailable);
        Assert.Equal("1.3.0-beta.1", check.Latest!.Version.ToString());
        Assert.True(check.Latest.IsPreRelease);
    }

    [Fact]
    public async Task Stable_channel_does_not_offer_a_newer_pre_release()
    {
        var json = Json(Rel("v1.3.0-beta.1", pre: true));

        var check = await CreateService(json).CheckAsync(Current, UpdateChannel.Stable);

        // The only release is a pre-release, so stable has nothing to offer.
        Assert.False(check.UpdateAvailable);
        Assert.Null(check.Latest);
    }

    [Fact]
    public async Task Drafts_are_ignored()
    {
        var json = Json(
            Rel("v9.9.9", draft: true),
            Rel("v1.2.0"));

        var check = await CreateService(json).CheckAsync(Current, UpdateChannel.Stable);

        Assert.Equal("1.2.0", check.Latest!.Version.ToString());
    }

    [Fact]
    public async Task Older_release_is_never_offered_as_an_update()
    {
        var json = Json(Rel("v1.0.0"));

        var check = await CreateService(json).CheckAsync(Current, UpdateChannel.Stable);

        Assert.False(check.UpdateAvailable);
        Assert.Equal("1.0.0", check.Latest!.Version.ToString());
    }

    [Fact]
    public async Task Same_version_is_not_an_update()
    {
        var json = Json(Rel("v1.1.3"));

        var check = await CreateService(json).CheckAsync(Current, UpdateChannel.Stable);

        Assert.False(check.UpdateAvailable);
    }

    [Fact]
    public async Task Highest_version_wins_over_publish_order()
    {
        var json = Json(
            Rel("v1.2.0"),
            Rel("v1.4.0"),
            Rel("v1.3.0"));

        var check = await CreateService(json).CheckAsync(Current, UpdateChannel.Stable);

        Assert.Equal("1.4.0", check.Latest!.Version.ToString());
    }

    [Fact]
    public async Task Tags_that_are_not_semantic_versions_are_skipped()
    {
        var json = Json(
            Rel("nightly"),
            Rel("v1.2.0"));

        var check = await CreateService(json).CheckAsync(Current, UpdateChannel.Stable);

        Assert.Equal("1.2.0", check.Latest!.Version.ToString());
    }

    [Fact]
    public async Task Assets_and_notes_are_exposed()
    {
        var json = Json(Rel(
            "v1.2.0",
            body: "Fixed things",
            assets: ("CTRL+Z-Portable-1.2.0.exe", "https://github.com/x/y.exe", 1234L, "sha256:" + new string('a', 64))));

        var check = await CreateService(json).CheckAsync(Current, UpdateChannel.Stable);

        var asset = Assert.Single(check.Latest!.Assets);
        Assert.Equal("CTRL+Z-Portable-1.2.0.exe", asset.Name);
        Assert.Equal(1234, asset.Size);
        Assert.Equal(new string('a', 64), asset.Sha256);
        Assert.Equal("Fixed things", check.Latest.Notes);
    }

    // ---- Failure handling ----------------------------------------------------------

    [Fact]
    public async Task Malformed_json_raises_a_friendly_failure()
    {
        var exception = await Assert.ThrowsAsync<UpdateCheckException>(
            () => CreateService("{not json").CheckAsync(Current, UpdateChannel.Stable));

        Assert.Equal(UpdateFailureKind.Malformed, exception.Kind);
        Assert.False(string.IsNullOrWhiteSpace(exception.FriendlyMessage));
    }

    [Fact]
    public async Task An_error_object_is_reported_as_malformed()
    {
        var exception = await Assert.ThrowsAsync<UpdateCheckException>(
            () => CreateService("""{"message":"API rate limit exceeded"}""").CheckAsync(Current, UpdateChannel.Stable));

        Assert.Equal(UpdateFailureKind.Malformed, exception.Kind);
        Assert.Contains("API rate limit exceeded", exception.Message);
    }

    [Fact]
    public async Task Empty_release_array_is_reported_as_not_found()
    {
        var exception = await Assert.ThrowsAsync<UpdateCheckException>(
            () => CreateService("[]").CheckAsync(Current, UpdateChannel.Stable));

        Assert.Equal(UpdateFailureKind.NotFound, exception.Kind);
    }

    [Fact]
    public async Task Rate_limit_failures_propagate_with_their_kind()
    {
        var service = new UpdateService(
            new FakeFeed(() => throw new UpdateCheckException(UpdateFailureKind.RateLimited, "limited")),
            TestFactory.Log);

        var exception = await Assert.ThrowsAsync<UpdateCheckException>(
            () => service.CheckAsync(Current, UpdateChannel.Stable));

        Assert.Equal(UpdateFailureKind.RateLimited, exception.Kind);
    }

    [Fact]
    public async Task Offline_failures_propagate_with_their_kind()
    {
        var service = new UpdateService(
            new FakeFeed(() => throw new UpdateCheckException(UpdateFailureKind.Offline, "offline")),
            TestFactory.Log);

        var exception = await Assert.ThrowsAsync<UpdateCheckException>(
            () => service.CheckAsync(Current, UpdateChannel.Stable));

        Assert.Equal(UpdateFailureKind.Offline, exception.Kind);
    }

    [Theory]
    [InlineData("sha256:" + "AB12", false)]
    [InlineData("sha256:" + "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("md5:0123456789abcdef0123456789abcdef", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Parses_asset_digests(string? digest, bool expectValue)
    {
        var parsed = UpdateService.ParseDigest(digest);

        Assert.Equal(expectValue, parsed is not null);
    }

    [Fact]
    public void Parsed_digest_is_lowercase_hex()
    {
        var parsed = UpdateService.ParseDigest("sha256:" + new string('A', 64));

        Assert.Equal(new string('a', 64), parsed);
    }

    // ---- Helpers -------------------------------------------------------------------

    private static UpdateService CreateService(string json) =>
        new(new FakeFeed(() => Task.FromResult(json)), TestFactory.Log);

    private static string Json(params string[] releases) => "[" + string.Join(",", releases) + "]";

    private static string Rel(
        string tag,
        bool pre = false,
        bool draft = false,
        string body = "",
        params (string Name, string Url, long Size, string? Digest)[] assets)
    {
        var assetJson = string.Join(",", assets.Select(asset =>
            $"{{\"name\":\"{asset.Name}\",\"browser_download_url\":\"{asset.Url}\"," +
            $"\"size\":{asset.Size},\"digest\":\"{asset.Digest}\"}}"));

        return $"{{\"tag_name\":\"{tag}\",\"name\":\"{tag}\",\"body\":\"{body}\"," +
               $"\"html_url\":\"https://github.com/IAM-GROOT1/ctrlz/releases/tag/{tag}\"," +
               $"\"published_at\":\"2026-01-01T00:00:00Z\"," +
               $"\"draft\":{draft.ToString().ToLowerInvariant()}," +
               $"\"prerelease\":{pre.ToString().ToLowerInvariant()}," +
               $"\"assets\":[{assetJson}]}}";
    }

    private sealed class FakeFeed : IReleaseFeed
    {
        private readonly Func<Task<string>> _get;

        public FakeFeed(Func<Task<string>> get) => _get = get;

        public Task<string> GetReleasesJsonAsync(CancellationToken cancellationToken = default) => _get();
    }
}
