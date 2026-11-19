// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Updates;

namespace CtrlZ.Core.Tests;

public class UpdateAssetTests
{
    private const string GitHubAsset = "https://github.com/IAM-GROOT1/ctrlz/releases/download/v1.2.0/";

    // ---- Asset selection -----------------------------------------------------------

    [Fact]
    public void Portable_copy_selects_the_portable_exe()
    {
        var release = Release("1.2.0",
            Asset("CTRL+Z-Portable-1.2.0.exe", GitHubAsset + "portable.exe", 1000),
            Asset("CTRL+Z-Setup-1.2.0.exe", GitHubAsset + "setup.exe", 2000));

        Assert.True(UpdateAssetSelector.TrySelect(release, UpdateInstallKind.Portable, out var selection, out var error), error);
        Assert.Equal("CTRL+Z-Portable-1.2.0.exe", selection!.Asset.Name);
    }

    [Fact]
    public void Installed_copy_selects_the_setup_exe()
    {
        var release = Release("1.2.0",
            Asset("CTRL+Z-Portable-1.2.0.exe", GitHubAsset + "portable.exe", 1000),
            Asset("CTRL+Z-Setup-1.2.0.exe", GitHubAsset + "setup.exe", 2000));

        Assert.True(UpdateAssetSelector.TrySelect(release, UpdateInstallKind.Installed, out var selection, out var error), error);
        Assert.Equal("CTRL+Z-Setup-1.2.0.exe", selection!.Asset.Name);
    }

    [Fact]
    public void Missing_asset_names_what_was_expected()
    {
        var release = Release("1.2.0",
            Asset("CTRL+Z-Portable-1.2.0.exe", GitHubAsset + "portable.exe", 1000));

        Assert.False(UpdateAssetSelector.TrySelect(release, UpdateInstallKind.Installed, out var selection, out var error));
        Assert.Null(selection);
        Assert.Contains("CTRL+Z-Setup-1.2.0.exe", error);
    }

    [Fact]
    public void Asset_for_a_different_version_is_not_selected()
    {
        var release = Release("1.2.0",
            Asset("CTRL+Z-Portable-1.1.9.exe", GitHubAsset + "old.exe", 1000));

        Assert.False(UpdateAssetSelector.TrySelect(release, UpdateInstallKind.Portable, out _, out var error));
        Assert.Contains("CTRL+Z-Portable-1.2.0.exe", error);
    }

    [Fact]
    public void Wrong_architecture_only_release_is_refused()
    {
        var release = Release("1.2.0",
            Asset("CTRL+Z-Portable-1.2.0-arm64.exe", GitHubAsset + "arm64.exe", 1000));

        Assert.False(UpdateAssetSelector.TrySelect(release, UpdateInstallKind.Portable, out _, out var error));
        Assert.Contains("x64", error);
    }

    [Fact]
    public void Asset_without_a_declared_size_is_refused()
    {
        var release = Release("1.2.0",
            Asset("CTRL+Z-Portable-1.2.0.exe", GitHubAsset + "portable.exe", 0));

        Assert.False(UpdateAssetSelector.TrySelect(release, UpdateInstallKind.Portable, out _, out var error));
        Assert.Contains("size", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("http://github.com/IAM-GROOT1/ctrlz/releases/download/v1.2.0/a.exe", false)]
    [InlineData("https://evil.example.com/a.exe", false)]
    [InlineData("https://github.com.evil.example.com/a.exe", false)]
    [InlineData("https://github.com/IAM-GROOT1/ctrlz/releases/download/v1.2.0/a.exe", true)]
    [InlineData("https://objects.githubusercontent.com/some/blob", true)]
    [InlineData("https://release-assets.githubusercontent.com/some/blob", true)]
    [InlineData("not a url", false)]
    [InlineData("", false)]
    public void Only_https_from_github_hosts_is_allowed(string url, bool allowed)
    {
        Assert.Equal(allowed, UpdateAssetSelector.IsAllowedDownloadUrl(url, out _));
    }

    [Fact]
    public void Asset_on_a_foreign_host_is_refused()
    {
        var release = Release("1.2.0",
            Asset("CTRL+Z-Portable-1.2.0.exe", "https://evil.example.com/portable.exe", 1000));

        Assert.False(UpdateAssetSelector.TrySelect(release, UpdateInstallKind.Portable, out _, out var error));
        Assert.Contains("evil.example.com", error);
    }

    [Fact]
    public void Finds_the_checksums_asset_case_insensitively()
    {
        var release = Release("1.2.0",
            Asset("sha256sums.txt", GitHubAsset + "sums", 100));

        Assert.NotNull(UpdateAssetSelector.FindChecksumsAsset(release));
    }

    // ---- Checksum file parsing -----------------------------------------------------

    [Fact]
    public void Finds_a_hash_for_a_listed_file()
    {
        var content = $"{Hash('b')}  CTRL+Z-Portable-1.2.0.exe\n{Hash('c')}  CTRL+Z-Setup-1.2.0.exe";

        Assert.Equal(Hash('c'), ChecksumFile.FindHash(content, "CTRL+Z-Setup-1.2.0.exe"));
    }

    [Fact]
    public void Handles_the_sha256sum_binary_marker()
    {
        var content = $"{Hash('d')} *CTRL+Z-Portable-1.2.0.exe";

        Assert.Equal(Hash('d'), ChecksumFile.FindHash(content, "CTRL+Z-Portable-1.2.0.exe"));
    }

    [Fact]
    public void Ignores_comments_blank_lines_and_malformed_hashes()
    {
        var content = $"# comment\n\nnot-a-hash  CTRL+Z-Portable-1.2.0.exe\n{Hash('e')}  CTRL+Z-Portable-1.2.0.exe";

        Assert.Equal(Hash('e'), ChecksumFile.FindHash(content, "CTRL+Z-Portable-1.2.0.exe"));
    }

    [Fact]
    public void Returns_null_when_the_file_is_not_listed()
    {
        var content = $"{Hash('f')}  CTRL+Z-Portable-1.2.0.exe";

        Assert.Null(ChecksumFile.FindHash(content, "CTRL+Z-Setup-1.2.0.exe"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Returns_null_for_empty_content(string? content)
    {
        Assert.Null(ChecksumFile.FindHash(content, "CTRL+Z-Portable-1.2.0.exe"));
    }

    [Fact]
    public void Uppercase_hashes_are_normalized_to_lowercase()
    {
        var content = $"{Hash('a').ToUpperInvariant()}  CTRL+Z-Portable-1.2.0.exe";

        Assert.Equal(Hash('a'), ChecksumFile.FindHash(content, "CTRL+Z-Portable-1.2.0.exe"));
    }

    // ---- Expected hash resolution --------------------------------------------------

    [Fact]
    public void Checksums_file_takes_precedence_over_the_api_digest()
    {
        var content = $"{Hash('1')}  CTRL+Z-Portable-1.2.0.exe";

        var resolved = UpdateVerifier.Resolve(content, "CTRL+Z-Portable-1.2.0.exe", Hash('2'));

        Assert.Equal(Hash('1'), resolved);
    }

    [Fact]
    public void Falls_back_to_the_api_digest()
    {
        var resolved = UpdateVerifier.Resolve(null, "CTRL+Z-Portable-1.2.0.exe", Hash('3'));

        Assert.Equal(Hash('3'), resolved);
    }

    [Fact]
    public void Returns_null_when_there_is_nothing_to_verify_against()
    {
        Assert.Null(UpdateVerifier.Resolve(null, "CTRL+Z-Portable-1.2.0.exe", null));
        Assert.Null(UpdateVerifier.Resolve("", "CTRL+Z-Portable-1.2.0.exe", "too-short"));
    }

    // ---- File verification ---------------------------------------------------------

    [Fact]
    public async Task Computes_the_sha256_of_a_real_file()
    {
        var directory = new TestDirectory();
        try
        {
            var path = directory.Write("payload.bin", "abc");

            var hash = await UpdateVerifier.ComputeSha256Async(path);

            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hash);
        }
        finally
        {
            directory.Dispose();
        }
    }

    [Fact]
    public async Task Missing_file_fails_verification()
    {
        var directory = new TestDirectory();
        try
        {
            var path = directory.PathOf("nope.bin");

            var exception = await Assert.ThrowsAsync<UpdateVerificationException>(
                () => UpdateVerifier.ComputeSha256Async(path));

            Assert.Contains("missing", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            directory.Dispose();
        }
    }

    [Fact]
    public void Hash_match_passes()
    {
        var expected = Hash('9');

        UpdateVerifier.EnsureHash(expected, expected.ToUpperInvariant());
    }

    [Fact]
    public void Hash_mismatch_is_refused()
    {
        var exception = Assert.Throws<UpdateVerificationException>(
            () => UpdateVerifier.EnsureHash(Hash('9'), Hash('8')));

        Assert.Contains("SHA-256", exception.Message);
    }

    [Fact]
    public void Size_match_passes()
    {
        UpdateVerifier.EnsureSize(1024, 1024);
    }

    [Fact]
    public void Size_mismatch_is_refused()
    {
        Assert.Throws<UpdateVerificationException>(() => UpdateVerifier.EnsureSize(1023, 1024));
    }

    [Fact]
    public void An_undeclared_size_is_not_checked()
    {
        UpdateVerifier.EnsureSize(1024, 0);
    }

    // ---- Helpers -------------------------------------------------------------------

    private static string Hash(char c) => new(c, 64);

    private static ReleaseInfo Release(string version, params UpdateAsset[] assets) =>
        new(
            TagName: "v" + version,
            Version: SemanticVersion.Parse(version),
            Title: version,
            Notes: string.Empty,
            HtmlUrl: "https://github.com/IAM-GROOT1/ctrlz/releases/tag/v" + version,
            PublishedAt: DateTimeOffset.UnixEpoch,
            IsPreRelease: false,
            Assets: assets);

    private static UpdateAsset Asset(string name, string url, long size) =>
        new(name, url, size, null);
}
