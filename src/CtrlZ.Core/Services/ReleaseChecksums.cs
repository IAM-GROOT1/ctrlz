// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Text.RegularExpressions;

namespace CtrlZ.Core.Services;

/// <summary>
/// Finds and reads a checksum file a project publishes next to its release assets
/// (<c>app.exe.sha256</c>, <c>SHA256SUMS</c>, <c>checksums.txt</c>, …), for releases where GitHub
/// itself reports no digest. Only SHA-256 is accepted: a line whose hash is not 64 hex characters is
/// ignored, so an MD5 or SHA-1 list never counts as verification.
/// </summary>
public static partial class ReleaseChecksums
{
    public const long MaxFileBytes = 1024 * 1024;

    private static readonly string[] ListNames =
    [
        "sha256sums", "sha256sums.txt", "sha256sum.txt", "checksums.txt", "checksums.sha256",
        "checksum.txt", "sha256.txt", "hashes.txt"
    ];

    /// <summary>
    /// The checksum asset for <paramref name="asset"/>: one named after it (<c>.sha256</c>,
    /// <c>.sha256sum</c>, <c>.sha256.txt</c>) first, then a release-wide list.
    /// </summary>
    public static GitHubAsset? FindFor(GitHubRelease release, GitHubAsset asset)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(asset);
        var candidates = release.Assets
            .Where(candidate => candidate.Size is > 0 and <= MaxFileBytes && candidate != asset)
            .ToList();

        foreach (var suffix in new[] { ".sha256", ".sha256sum", ".sha256.txt" })
        {
            var own = candidates.FirstOrDefault(candidate =>
                candidate.Name.Equals(asset.Name + suffix, StringComparison.OrdinalIgnoreCase));
            if (own is not null)
            {
                return own;
            }
        }

        foreach (var listName in ListNames)
        {
            var list = candidates.FirstOrDefault(candidate => candidate.Name.Equals(listName, StringComparison.OrdinalIgnoreCase));
            if (list is not null)
            {
                return list;
            }
        }

        return candidates.FirstOrDefault(candidate =>
            ListSuffix().IsMatch(candidate.Name));
    }

    /// <summary>
    /// The SHA-256 the file lists for <paramref name="assetName"/>, or null when it does not list it.
    /// A file named after one asset may hold just the hash. Two different hashes for the same name
    /// are a conflict and throw, rather than picking one.
    /// </summary>
    /// <exception cref="FormatException">The file lists the asset with conflicting hashes.</exception>
    public static string? Parse(string text, string assetName, bool fileIsForThisAsset)
    {
        ArgumentNullException.ThrowIfNull(text);
        string? found = null;
        string? bare = null;
        var bareCount = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            string? hash = null;
            string? name = null;
            if (BsdLine().Match(line) is { Success: true } bsd)
            {
                (hash, name) = (bsd.Groups["hash"].Value, bsd.Groups["name"].Value);
            }
            else if (GnuLine().Match(line) is { Success: true } gnu)
            {
                (hash, name) = (gnu.Groups["hash"].Value, gnu.Groups["name"].Value);
            }
            else if (BareHash().IsMatch(line))
            {
                bare = line;
                bareCount++;
                continue;
            }

            if (hash is null || name is null || !SameFile(name, assetName))
            {
                continue;
            }

            hash = hash.ToLowerInvariant();
            if (found is not null && found != hash)
            {
                throw new FormatException($"The checksum file lists {assetName} with two different SHA-256 values.");
            }

            found = hash;
        }

        if (found is null && fileIsForThisAsset && bareCount == 1)
        {
            found = bare!.ToLowerInvariant();
        }

        return found;
    }

    /// <summary>True when the checksum asset is named after this one asset rather than being a list.</summary>
    public static bool IsForAsset(GitHubAsset checksum, GitHubAsset asset) =>
        checksum.Name.StartsWith(asset.Name + ".", StringComparison.OrdinalIgnoreCase);

    private static bool SameFile(string listed, string assetName)
    {
        var name = listed.Trim().TrimStart('*');
        if (name.StartsWith("./", StringComparison.Ordinal))
        {
            name = name[2..];
        }

        return name.Equals(assetName, StringComparison.OrdinalIgnoreCase);
    }

    // "<hash>  name", "<hash> *name" (binary mode) or "<hash> name".
    [GeneratedRegex(@"^(?<hash>[0-9A-Fa-f]{64})\s+\*?(?<name>\S.*)$")]
    private static partial Regex GnuLine();

    // "SHA256 (name) = <hash>"
    [GeneratedRegex(@"^SHA256\s*\((?<name>.+)\)\s*=\s*(?<hash>[0-9A-Fa-f]{64})$", RegexOptions.IgnoreCase)]
    private static partial Regex BsdLine();

    [GeneratedRegex("^[0-9A-Fa-f]{64}$")]
    private static partial Regex BareHash();

    [GeneratedRegex(@"[._-](sha256sums?|checksums?)(\.txt)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ListSuffix();
}
