// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Models;
using CtrlZ.Core.Services;
using ICSharpCode.SharpZipLib.Zip;

namespace CtrlZ.Core.Tests;

public class BrowserArchiveVerifierTests
{
    private static async Task<(TestDirectory Temp, string Archive)> Backup(string? password)
    {
        var (temp, _, browser) = BrowserBackupTests.CreateProfileTree();
        var result = await BrowserBackupTests.CreateService(temp).BackupAsync(new BrowserBackupRequest(
            browser, browser.Profiles, temp.PathOf("backups"), Password: password));
        return (temp, result.ArchivePath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("correct horse")]
    public async Task A_fresh_backup_is_restorable(string? password)
    {
        var (temp, archive) = await Backup(password);
        using (temp)
        {
            var result = new BrowserArchiveVerifier().Verify(archive, password);

            Assert.True(result.IsRestorable, string.Join("; ", result.Problems));
            Assert.True(result.FilesChecked > 0);
            Assert.True(result.ProfilesChecked > 0);
            Assert.StartsWith("Restorable", result.Summary);
        }
    }

    [Fact]
    public async Task A_wrong_or_missing_password_is_reported_as_such()
    {
        var (temp, archive) = await Backup("correct horse");
        using (temp)
        {
            Assert.Throws<BrowserArchivePasswordException>(() => new BrowserArchiveVerifier().Verify(archive, "wrong"));
            Assert.Throws<BrowserArchivePasswordException>(() => new BrowserArchiveVerifier().Verify(archive, null));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("correct horse")]
    public async Task Damaged_bytes_inside_a_file_are_found(string? password)
    {
        var (temp, archive) = await Backup(password);
        using (temp)
        {
            // Corrupt the data of the largest stored file, leaving the directory intact.
            long offset;
            using (var zip = new ZipFile(archive))
            {
                var largest = zip.Cast<ZipEntry>().Where(e => e.IsFile && e.Name != "manifest.json")
                    .OrderByDescending(e => e.CompressedSize).First();
                offset = largest.Offset + 30 + largest.Name.Length + (largest.ExtraData?.Length ?? 0) + largest.CompressedSize / 2;
            }

            using (var stream = new FileStream(archive, FileMode.Open, FileAccess.ReadWrite))
            {
                stream.Position = offset;
                var original = stream.ReadByte();
                stream.Position = offset;
                stream.WriteByte((byte)(original ^ 0xFF));
            }

            var result = new BrowserArchiveVerifier().Verify(archive, password);

            Assert.False(result.IsRestorable);
            Assert.Contains(result.Problems, problem => problem.Contains("damaged"));
        }
    }

    [Fact]
    public async Task A_profile_with_no_files_and_planted_secrets_are_both_reported()
    {
        var (temp, archive) = await Backup(null);
        using (temp)
        {
            using (var zip = new ZipFile(archive))
            {
                zip.BeginUpdate();
                foreach (var entry in zip.Cast<ZipEntry>().Where(e => e.Name.StartsWith("User Data/Default/")).ToList())
                {
                    zip.Delete(entry);
                }

                zip.Add(new StaticSource("secret"), "User Data/Other/Login Data");
                zip.CommitUpdate();
            }

            var result = new BrowserArchiveVerifier().Verify(archive, null);

            Assert.False(result.IsRestorable);
            Assert.Contains(result.Problems, problem => problem.Contains("no files in the archive"));
            Assert.Contains(result.Problems, problem => problem.Contains("never backs up"));
        }
    }

    [Fact]
    public void A_file_that_is_not_an_archive_is_not_restorable()
    {
        using var temp = new TestDirectory();
        var path = temp.Write("not-a-zip.zip", "hello");

        var result = new BrowserArchiveVerifier().Verify(path, null);

        Assert.False(result.IsRestorable);
        Assert.NotEmpty(result.Problems);
    }

    private sealed class StaticSource(string text) : IStaticDataSource
    {
        public Stream GetSource() => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
    }
}
