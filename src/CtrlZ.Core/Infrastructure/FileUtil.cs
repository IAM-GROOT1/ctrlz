// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Security.Cryptography;
using System.Text;

namespace CtrlZ.Core.Infrastructure;

public static class FileUtil
{
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    public static void AtomicWriteAllText(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + "." + Guid.NewGuid().ToString("n")[..8] + ".tmp";
        try
        {
            File.WriteAllText(temp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch { /* best effort */ }
            }
        }
    }

    public static string SafeFileName(string? value, string fallback = "item", int maxLength = 80)
    {
        var cleaned = new string((value ?? string.Empty)
            .Select(c => InvalidFileNameChars.Contains(c) || char.IsControl(c) ? '-' : c)
            .ToArray())
            .Trim(' ', '.', '-');

        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = fallback;
        }

        if (cleaned.Length > maxLength)
        {
            cleaned = cleaned[..maxLength].TrimEnd(' ', '.', '-');
        }

        return cleaned;
    }

    public static string Slug(string? value, string fallback = "app")
    {
        var chars = (value ?? string.Empty).ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')
            .ToArray();
        var slug = new string(chars);
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        slug = slug.Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? fallback : slug[..Math.Min(slug.Length, 60)];
    }

    public static bool IsUnderDirectory(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    public static long DirectorySize(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; } catch { /* vanished or inaccessible */ }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return total;
    }

    public static async Task<string> Sha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }
}
