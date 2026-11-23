// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Localization;
using CtrlZ.Core.Models;
using ICSharpCode.SharpZipLib.Zip;

namespace CtrlZ.Core.Services;

/// <summary>Result of reading a whole browser backup the way a restore would.</summary>
public sealed record BrowserArchiveVerification(
    bool IsRestorable,
    string BrowserName,
    int ProfilesChecked,
    long FilesChecked,
    long BytesChecked,
    IReadOnlyList<string> Problems)
{
    public string Summary => IsRestorable
        ? CoreText.Plural("BrowserArchiveVerifier_Restorable", ProfilesChecked, BrowserName) +
          CoreText.Format("BrowserArchiveVerifier_FilesReadAndChecked", FilesChecked)
        : CoreText.Plural("BrowserArchiveVerifier_NotRestorable", Problems.Count);
}

/// <summary>
/// Proves a browser backup can be restored before it is needed: opens it with its password, reads
/// the manifest, reads every file to the end so each CRC is checked, and confirms every profile
/// the manifest lists is present and that nothing excluded from backups (passwords, cookies) is in
/// it. Reads only; never writes or extracts anything.
/// </summary>
public sealed class BrowserArchiveVerifier
{
    private readonly BrowserArchiveReader _reader;

    public BrowserArchiveVerifier(BrowserArchiveReader? reader = null)
    {
        _reader = reader ?? new BrowserArchiveReader();
    }

    /// <exception cref="BrowserArchivePasswordException">The password is missing or wrong.</exception>
    public BrowserArchiveVerification Verify(string archivePath, string? password, CancellationToken cancellationToken = default)
    {
        BrowserArchiveInfo info;
        try
        {
            info = _reader.ReadInfo(archivePath, password);
        }
        catch (BrowserArchivePasswordException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            return new BrowserArchiveVerification(false, string.Empty, 0, 0, 0, [ex.Message]);
        }

        var problems = new List<string>();
        var names = new List<string>();
        long files = 0;
        long bytes = 0;

        try
        {
            using var zip = new ZipFile(archivePath);
            if (!string.IsNullOrWhiteSpace(password))
            {
                zip.Password = password;
            }

            var buffer = new byte[81920];
            foreach (ZipEntry entry in zip)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!entry.IsFile)
                {
                    continue;
                }

                names.Add(entry.Name.Replace('\\', '/'));
                try
                {
                    // Read to the end: AES entries check their authentication code at the end of
                    // the stream; every entry that stores a CRC-32 is compared here.
                    var crc = new ICSharpCode.SharpZipLib.Checksum.Crc32();
                    using (var stream = zip.GetInputStream(entry))
                    {
                        int read;
                        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            crc.Update(new ArraySegment<byte>(buffer, 0, read));
                            bytes += read;
                        }
                    }

                    if (entry.HasCrc && entry.Crc != 0 && crc.Value != entry.Crc)
                    {
                        problems.Add(CoreText.Format("BrowserArchiveVerifier_TheFileIsDamagedIts", entry.Name));
                        continue;
                    }

                    files++;
                }
                catch (Exception ex) when (ex is ZipException or IOException or InvalidDataException)
                {
                    problems.Add(CoreText.Format("BrowserArchiveVerifier_TheFileIsDamagedAnd", entry.Name, ex.GetType().Name));
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ZipException or IOException or UnauthorizedAccessException)
        {
            problems.Add(CoreText.Get("BrowserArchiveVerifier_TheArchiveCouldNotBe") + ex.Message);
        }

        foreach (var profile in info.Manifest.Profiles)
        {
            var prefix = info.Manifest.FlatProfileLayout ? "User Data/" : $"User Data/{profile.DirectoryName}/";
            if (!names.Any(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add(CoreText.Format("BrowserArchiveVerifier_ProfileIsListedInThe", profile.DisplayName));
            }
        }

        var forbidden = names.Where(BrowserArchiveReader.IsForbiddenRestorePath).Take(5).ToList();
        if (forbidden.Count > 0)
        {
            problems.Add(CoreText.Get("BrowserArchiveVerifier_TheArchiveContainsFilesCTRL") +
                         string.Join(", ", forbidden) + ". It may have been changed after it was made.");
        }

        return new BrowserArchiveVerification(
            problems.Count == 0,
            info.Manifest.BrowserName,
            info.Manifest.Profiles.Count,
            files,
            bytes,
            problems);
    }
}
