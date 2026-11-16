// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Text;
using System.Text.Json;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Models;
using ICSharpCode.SharpZipLib.Zip;

namespace CtrlZ.Core.Services;

/// <summary>Reads and validates CTRL+Z browser backup archives, including encrypted archives.</summary>
public sealed class BrowserArchiveReader
{
    private const string ManifestName = "manifest.json";
    private const long MaxManifestBytes = 10 * 1024 * 1024;

    public BrowserArchiveInfo ReadInfo(string archivePath, string? password)
    {
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("The browser backup archive was not found.", archivePath);
        }

        try
        {
            using var zip = new ZipFile(archivePath);
            var encrypted = IsEncrypted(zip);
            if (encrypted && string.IsNullOrWhiteSpace(password))
            {
                throw new BrowserArchivePasswordException("This archive is encrypted. Enter its password to continue.");
            }

            if (!string.IsNullOrWhiteSpace(password))
            {
                zip.Password = password;
            }

            var entry = zip.GetEntry(ManifestName)
                        ?? throw new InvalidDataException("This ZIP is not a CTRL+Z browser backup (manifest.json is missing).");
            if (entry.Size > MaxManifestBytes)
            {
                throw new InvalidDataException("The archive manifest is unexpectedly large.");
            }

            string manifestJson;
            try
            {
                using var stream = zip.GetInputStream(entry);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                manifestJson = reader.ReadToEnd();
            }
            catch (ZipException) when (encrypted)
            {
                throw new BrowserArchivePasswordException("The archive password is incorrect.");
            }

            var manifest = JsonSerializer.Deserialize<BrowserBackupManifest>(manifestJson, JsonDefaults.Profile)
                           ?? throw new InvalidDataException("The archive manifest is empty.");
            if (manifest.Format is not ("browser-profile-backup" or "chromium-profile-backup") ||
                manifest.SchemaVersion > BrowserBackupManifest.CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    "This archive was created by an unsupported version of CTRL+Z or is not a browser backup.");
            }

            return new BrowserArchiveInfo(archivePath, manifest, encrypted);
        }
        catch (BrowserArchivePasswordException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (ZipException ex)
        {
            throw new InvalidDataException("The browser backup archive is damaged or encrypted with an unknown format.", ex);
        }
    }

    public static bool IsEncrypted(ZipFile zip)
    {
        for (var i = 0; i < zip.Count; i++)
        {
            var entry = zip[i];
            if (entry?.IsCrypted == true)
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsForbiddenRestorePath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part.Equals("Network", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var fileName = parts.LastOrDefault() ?? normalized;
        return BrowserDataRules.IsForbiddenFile(fileName) ||
               FirefoxDataRules.IsForbiddenFile(fileName) ||
               fileName.Equals("Cookies", StringComparison.OrdinalIgnoreCase) ||
               fileName.StartsWith("Login Data", StringComparison.OrdinalIgnoreCase);
    }
}
