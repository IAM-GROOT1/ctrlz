using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Services;

namespace CtrlZ.Core.Updates;

public sealed class UpdateVerificationException : Exception
{
    public UpdateVerificationException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Parses a <c>SHA256SUMS.txt</c> published alongside the release assets.</summary>
public static class ChecksumFile
{
    public const int Sha256HexLength = 64;

    /// <summary>
    /// Returns the lowercase SHA-256 for <paramref name="fileName"/>, or null when the file
    /// is absent from the checksum listing. Accepts <c>sha256sum</c> output
    /// (<c>&lt;hex&gt;  name</c>) and its binary marker form (<c>&lt;hex&gt; *name</c>).
    /// </summary>
    public static string? FindHash(string? content, string fileName)
    {
        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf(' ');
            if (separator <= 0)
            {
                continue;
            }

            var hash = line[..separator];
            if (!IsSha256(hash))
            {
                continue;
            }

            var name = line[(separator + 1)..].Trim();
            if (name.StartsWith('*'))
            {
                name = name[1..];
            }

            if (string.Equals(Path.GetFileName(name), fileName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
            {
                return hash.ToLowerInvariant();
            }
        }

        return null;
    }

    public static bool IsSha256(string? value)
    {
        if (value is null || value.Length != Sha256HexLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Decides which hash a download must match and re-checks the file before it is applied.
/// </summary>
public static class UpdateVerifier
{
    /// <summary>
    /// The checksums file wins; the API asset digest is the fallback. Null means the release
    /// published nothing to verify against, and the updater must then refuse to continue.
    /// </summary>
    public static string? Resolve(string? checksumsContent, string assetName, string? apiDigest)
    {
        var fromFile = ChecksumFile.FindHash(checksumsContent, assetName);
        if (fromFile is not null)
        {
            return fromFile;
        }

        return ChecksumFile.IsSha256(apiDigest) ? apiDigest!.ToLowerInvariant() : null;
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            throw new UpdateVerificationException(
                $"The downloaded update is missing ({Path.GetFileName(path)}). Nothing was applied.");
        }

        return await FileUtil.Sha256Async(path, cancellationToken).ConfigureAwait(false);
    }

    public static void EnsureSize(long actual, long declared)
    {
        if (declared <= 0)
        {
            return;
        }

        if (actual != declared)
        {
            throw new UpdateVerificationException(
                $"The download is {actual} bytes but the release declares {declared}. Nothing was applied.");
        }
    }

    public static void EnsureHash(string expected, string actual)
    {
        var normalizedExpected = DownloadService.NormalizeHash(expected);
        var normalizedActual = DownloadService.NormalizeHash(actual);

        if (!string.Equals(normalizedExpected, normalizedActual, StringComparison.Ordinal))
        {
            throw new UpdateVerificationException(
                "The downloaded update failed its SHA-256 check. It was deleted and nothing was applied.");
        }
    }
}
