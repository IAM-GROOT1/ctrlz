// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Updates;

namespace CtrlZ.Core.Tests;

public class GitHubReleaseFeedTests
{
    // ---- Debug repository override ------------------------------------------------
    // Only the environment lookup is DEBUG-only; these rules are compiled in every build so
    // they can be verified in CI, where the tests run in Release.

    [Theory]
    [InlineData("owner/repo", "owner/repo")]
    [InlineData("  owner/repo  ", "owner/repo")]
    [InlineData("Some_User/Some.Repo-name", "Some_User/Some.Repo-name")]
    [InlineData("IAM-GROOT1/ctrlz", "IAM-GROOT1/ctrlz")]
    public void Accepts_exactly_owner_slash_name(string input, string expected)
    {
        Assert.True(GitHubReleaseFeed.TryParseRepositoryOverride(input, out var repository));
        Assert.Equal(expected, repository);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("owner")]                                        // no name
    [InlineData("owner/repo/extra")]                             // extra segment
    [InlineData("https://evil.example.com/owner/repo")]          // scheme + host
    [InlineData("http://github.com/owner/repo")]                 // scheme + host
    [InlineData("evil.example.com/owner/repo")]                  // three segments
    [InlineData("owner/repo?per_page=100")]                      // query
    [InlineData("owner/repo#frag")]                              // fragment
    [InlineData("owner:repo")]                                   // colon
    [InlineData(@"owner\repo")]                                  // backslash
    [InlineData("owner@host/repo")]                              // userinfo
    [InlineData("../etc/passwd")]                                // traversal
    [InlineData("owner/..")]                                     // traversal segment
    [InlineData("..")]                                           // traversal segment
    [InlineData(".")]                                            // current dir
    [InlineData("owner/na..me")]                                 // consecutive dots
    [InlineData("own er/repo")]                                  // space
    [InlineData("owner/re po")]
    public void Rejects_anything_that_is_not_a_bare_owner_slash_name(string? input)
    {
        Assert.False(GitHubReleaseFeed.TryParseRepositoryOverride(input, out var repository));

        // On rejection the caller must fall back to production, never to the bad value.
        Assert.Equal(GitHubReleaseFeed.ProductionRepository, repository);
    }

    [Fact]
    public void Production_repository_is_the_official_one()
    {
        Assert.Equal("IAM-GROOT1/ctrlz", GitHubReleaseFeed.ProductionRepository);
    }

    // ---- URL shape ----------------------------------------------------------------

    [Fact]
    public void The_feed_url_is_always_a_github_api_releases_url()
    {
        var uri = GitHubReleaseFeed.ReleasesUri;

        Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
        Assert.Equal("api.github.com", uri.Host);

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, segments.Length);
        Assert.Equal("repos", segments[0]);
        Assert.Equal("releases", segments[3]);
    }

    // ---- Asset hosts stay restricted independently of the feed ---------------------

    [Theory]
    [InlineData("https://downloads.example.com/CTRL+Z-Portable-1.2.0.exe")]
    [InlineData("https://example.com/github.com/x.exe")]
    [InlineData("http://github.com/IAM-GROOT1/ctrlz/releases/download/v1.2.0/a.exe")]
    [InlineData("https://github.com.attacker.example/a.exe")]
    public void Repository_override_cannot_widen_the_asset_host_allow_list(string url)
    {
        // The override only changes the API path; asset URLs are validated separately and
        // remain limited to GitHub's own download hosts over HTTPS.
        Assert.False(UpdateAssetSelector.IsAllowedDownloadUrl(url, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("https://github.com/IAM-GROOT1/ctrlz/releases/download/v1.2.0/a.exe")]
    [InlineData("https://objects.githubusercontent.com/some/blob")]
    public void GitHub_download_hosts_remain_allowed(string url)
    {
        Assert.True(UpdateAssetSelector.IsAllowedDownloadUrl(url, out _));
    }
}
