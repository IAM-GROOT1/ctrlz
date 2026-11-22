// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Runtime.InteropServices;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Install;
using CtrlZ.Core.Models;
using CtrlZ.Core.Services;

namespace CtrlZ.Core.Tests;

public class InstallerSignatureTests
{
    private sealed class FixedVerifier(SignatureStatus status) : IInstallerSignatureVerifier
    {
        public int Calls { get; private set; }

        public SignatureCheck Check(string path)
        {
            Calls++;
            return new SignatureCheck(status, "Test Publisher", status == SignatureStatus.Broken ? unchecked((int)0x80096010) : 0);
        }
    }

    // A runtime DLL with an embedded Microsoft signature, present wherever the tests run.
    private static string SignedRuntimeFile => Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "coreclr.dll");

    [Theory]
    [InlineData(0, SignatureStatus.Valid)]
    [InlineData(unchecked((int)0x800B0100), SignatureStatus.Unsigned)]
    [InlineData(unchecked((int)0x80096010), SignatureStatus.Broken)]
    [InlineData(unchecked((int)0x80096004), SignatureStatus.Broken)]
    [InlineData(unchecked((int)0x800B010C), SignatureStatus.Broken)]
    [InlineData(unchecked((int)0x800B0111), SignatureStatus.Broken)]
    [InlineData(unchecked((int)0x800B0109), SignatureStatus.Untrusted)]
    [InlineData(unchecked((int)0x800B0101), SignatureStatus.Untrusted)]
    [InlineData(unchecked((int)0x800B0003), SignatureStatus.NotChecked)]
    [InlineData(unchecked((int)0x8009200D), SignatureStatus.NotChecked)] // CRYPT_E_BAD_MSG: malformed, not "intact"
    [InlineData(unchecked((int)0x800B010A), SignatureStatus.Untrusted)]
    public void Codes_map_to_statuses(int code, SignatureStatus expected) =>
        Assert.Equal(expected, AuthenticodeVerifier.Classify(code));

    [Fact]
    public void Only_a_broken_signature_blocks()
    {
        foreach (var status in Enum.GetValues<SignatureStatus>())
        {
            Assert.Equal(status == SignatureStatus.Broken, new SignatureCheck(status, null, 0).BlocksInstall);
        }
    }

    [Fact]
    public void A_signed_runtime_file_has_an_intact_signature_and_a_publisher()
    {
        var check = AuthenticodeVerifier.Instance.Check(SignedRuntimeFile);

        Assert.Contains(check.Status, new[] { SignatureStatus.Valid, SignatureStatus.Untrusted });
        Assert.Contains("Microsoft", check.Publisher ?? string.Empty);
    }

    [Fact]
    public void Changing_one_byte_of_a_signed_file_breaks_its_signature()
    {
        using var temp = new TestDirectory();
        var copy = temp.PathOf("tampered.dll");
        File.Copy(SignedRuntimeFile, copy);
        var bytes = File.ReadAllBytes(copy);
        bytes[bytes.Length / 3] ^= 0xFF;
        File.WriteAllBytes(copy, bytes);

        var check = AuthenticodeVerifier.Instance.Check(copy);

        Assert.Equal(SignatureStatus.Broken, check.Status);
        Assert.True(check.BlocksInstall);
    }

    [Fact]
    public void A_file_that_is_not_a_program_is_not_blocked()
    {
        using var temp = new TestDirectory();
        var path = temp.PathOf("notes.exe");
        File.WriteAllText(path, "not a program");

        var check = AuthenticodeVerifier.Instance.Check(path);

        Assert.False(check.BlocksInstall);
        Assert.Contains(check.Status, new[] { SignatureStatus.NotChecked, SignatureStatus.Unsigned });
    }

    private static (FileInstallService Service, InstallContext Context, FakeInstallLogSink Sink) Create(
        TestDirectory temp, IInstallerSignatureVerifier verifier)
    {
        var paths = new AppPaths(temp.PathOf("data"));
        paths.EnsureCreated();
        var sink = new FakeInstallLogSink();
        return (new FileInstallService(paths, new ProcessRunner(), TestFactory.Log, signatures: verifier),
            new InstallContext(paths, sink, TestFactory.Log, false), sink);
    }

    [Fact]
    public async Task A_broken_signature_stops_the_installer_from_running()
    {
        using var temp = new TestDirectory();
        var marker = temp.PathOf("ran.txt");
        var verifier = new FixedVerifier(SignatureStatus.Broken);
        var (service, context, sink) = Create(temp, verifier);
        var app = new AppEntry { Name = "Fake", Source = AppSource.LocalFile, SilentArgs = $"/c echo x > \"{marker}\"" };

        var result = await service.RunInstallerAsync(app, Path.Combine(Environment.SystemDirectory, "cmd.exe"), context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(1, verifier.Calls);
        Assert.False(File.Exists(marker));
        Assert.Contains(sink.Lines, line => line.Contains("signature is broken", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(SignatureStatus.Unsigned)]
    [InlineData(SignatureStatus.Untrusted)]
    [InlineData(SignatureStatus.NotChecked)]
    [InlineData(SignatureStatus.Valid)]
    public async Task Other_results_are_logged_and_the_install_continues(SignatureStatus status)
    {
        using var temp = new TestDirectory();
        var (service, context, sink) = Create(temp, new FixedVerifier(status));
        var app = new AppEntry { Name = "Fake", Source = AppSource.LocalFile, SilentArgs = "/c exit 0" };

        var result = await service.RunInstallerAsync(app, Path.Combine(Environment.SystemDirectory, "cmd.exe"), context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains(new SignatureCheck(status, "Test Publisher", 0).Describe(), sink.Lines);
    }

    [Fact]
    public async Task A_portable_exe_with_a_broken_signature_is_not_copied()
    {
        using var temp = new TestDirectory();
        var (service, context, _) = Create(temp, new FixedVerifier(SignatureStatus.Broken));
        var exe = temp.PathOf("tool.exe");
        File.WriteAllText(exe, "x");
        var app = new AppEntry { Name = "Tool", Source = AppSource.LocalFile };

        var result = await service.InstallAsync(app, exe, GitHubAssetKind.PortableExecutable, context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(Directory.Exists(Path.Combine(temp.PathOf("data"), "apps", "tool")));
    }
}

public class ReleaseChecksumsTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Other = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    private static GitHubAsset Asset(string name, long size = 100) => new(name, "https://example.invalid/" + name, size, null, null);

    private static GitHubRelease Release(params GitHubAsset[] assets) =>
        new("o/r", "v1", "v1", "https://github.com/o/r", null, assets);

    [Fact]
    public void A_file_named_after_the_asset_wins_over_a_list()
    {
        var setup = Asset("setup.exe", 5_000_000);
        var release = Release(setup, Asset("SHA256SUMS"), Asset("setup.exe.sha256"));

        Assert.Equal("setup.exe.sha256", ReleaseChecksums.FindFor(release, setup)!.Name);
    }

    [Theory]
    [InlineData("SHA256SUMS")]
    [InlineData("sha256sums.txt")]
    [InlineData("checksums.txt")]
    [InlineData("tool_1.2.3_checksums.txt")]
    [InlineData("tool-1.2.3-SHA256SUMS")]
    public void Release_wide_lists_are_found(string name)
    {
        var setup = Asset("setup.exe", 5_000_000);

        Assert.Equal(name, ReleaseChecksums.FindFor(Release(setup, Asset(name)), setup)!.Name);
    }

    [Fact]
    public void Oversized_or_unrelated_files_are_not_used()
    {
        var setup = Asset("setup.exe", 5_000_000);
        var release = Release(setup, Asset("checksums.txt", ReleaseChecksums.MaxFileBytes + 1), Asset("other.exe.sha256"), Asset("notes.txt"));

        Assert.Null(ReleaseChecksums.FindFor(release, setup));
    }

    [Theory]
    [InlineData(Hash + "  setup.exe")]
    [InlineData(Hash + " *setup.exe")]
    [InlineData(Hash + "  ./setup.exe")]
    [InlineData("SHA256 (setup.exe) = " + Hash)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF  SETUP.EXE")]
    public void List_formats_are_read(string line) =>
        Assert.Equal(Hash, ReleaseChecksums.Parse("# comment\n" + Other + "  other.zip\n" + line + "\n", "setup.exe", false));

    [Fact]
    public void Md5_and_sha1_lines_never_count()
    {
        const string text = "d41d8cd98f00b204e9800998ecf8427e  setup.exe\nda39a3ee5e6b4b0d3255bfef95601890afd80709  setup.exe\n";

        Assert.Null(ReleaseChecksums.Parse(text, "setup.exe", true));
    }

    [Fact]
    public void A_bare_hash_counts_only_in_the_assets_own_file()
    {
        Assert.Equal(Hash, ReleaseChecksums.Parse(Hash + "\n", "setup.exe", true));
        Assert.Null(ReleaseChecksums.Parse(Hash + "\n", "setup.exe", false));
        Assert.Null(ReleaseChecksums.Parse(Hash + "\n" + Other + "\n", "setup.exe", true));
    }

    [Fact]
    public void Two_hashes_for_the_same_file_are_a_conflict() =>
        Assert.Throws<FormatException>(() =>
            ReleaseChecksums.Parse(Hash + "  setup.exe\n" + Other + "  setup.exe\n", "setup.exe", false));

    [Fact]
    public void A_checksum_of_a_longer_name_is_not_the_assets_own_file()
    {
        var exe = Asset("app.exe", 5_000_000);
        var zipSum = Asset("app.exe.zip.sha256sum");

        Assert.False(ReleaseChecksums.IsForAsset(zipSum, exe));
        Assert.True(ReleaseChecksums.IsForAsset(Asset("app.exe.sha256"), exe));
        Assert.Null(ReleaseChecksums.Parse(Hash + "\n", "app.exe", ReleaseChecksums.IsForAsset(zipSum, exe)));
    }

    [Fact]
    public void Every_candidate_list_is_offered_in_order()
    {
        var setup = Asset("setup.exe", 5_000_000);
        var release = Release(setup, Asset("tool-windows-sha256sums.txt"), Asset("checksums.txt"), Asset("setup.exe.sha256"));

        Assert.Equal(["setup.exe.sha256", "checksums.txt", "tool-windows-sha256sums.txt"],
            ReleaseChecksums.CandidatesFor(release, setup).Select(asset => asset.Name));
    }

    [Fact]
    public void Utf8_bom_and_utf16_files_are_decoded()
    {
        var line = Hash + "  setup.exe\r\n";
        var utf8Bom = System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(line)).ToArray();
        var utf16 = System.Text.Encoding.Unicode.GetPreamble().Concat(System.Text.Encoding.Unicode.GetBytes(line)).ToArray();

        Assert.Equal(Hash, ReleaseChecksums.Parse(ReleaseChecksums.Decode(utf8Bom), "setup.exe", false));
        Assert.Equal(Hash, ReleaseChecksums.Parse(ReleaseChecksums.Decode(utf16), "setup.exe", false));
        Assert.Equal(Hash, ReleaseChecksums.Parse(ReleaseChecksums.Decode(
            System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.ASCII.GetBytes(Hash)).ToArray()), "setup.exe", true));
    }

    [Fact]
    public void A_list_without_the_asset_gives_nothing() =>
        Assert.Null(ReleaseChecksums.Parse(Hash + "  setup-arm64.exe\n", "setup.exe", false));
}
