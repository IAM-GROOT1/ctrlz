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

    private static readonly string[] OwnSuffixes = [".sha256", ".sha256sum", ".sha256.txt"];

    /// <summary>Most likely checksum asset for <paramref name="asset"/>; see <see cref="CandidatesFor"/>.</summary>
    public static GitHubAsset? FindFor(GitHubRelease release, GitHubAsset asset) =>
        CandidatesFor(release, asset).FirstOrDefault();

    /// <summary>
    /// Checksum assets that may list <paramref name="asset"/>, most specific first: one named after
    /// it (<c>.sha256</c>, <c>.sha256sum</c>, <c>.sha256.txt</c>), then well-known list names, then
    /// other <c>*-checksums.txt</c>-style lists. Each is at most <see cref="MaxFileBytes"/>.
    /// </summary>
    public static IReadOnlyList<GitHubAsset> CandidatesFor(GitHubRelease release, GitHubAsset asset)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(asset);
        var candidates = release.Assets
            .Where(candidate => candidate.Size is > 0 and <= MaxFileBytes && candidate != asset)
            .ToList();

        var ordered = new List<GitHubAsset>();
        foreach (var suffix in OwnSuffixes)
        {
            ordered.AddRange(candidates.Where(candidate =>
                candidate.Name.Equals(asset.Name + suffix, StringComparison.OrdinalIgnoreCase)));
        }

        foreach (var listName in ListNames)
        {
            ordered.AddRange(candidates.Where(candidate => candidate.Name.Equals(listName, StringComparison.OrdinalIgnoreCase)));
        }

        ordered.AddRange(candidates.Where(candidate => ListSuffix().IsMatch(candidate.Name)));
        return ordered.Distinct().ToList();
    }

    /// <summary>
    /// Reads a checksum file's bytes: UTF-8 (with or without a BOM) or UTF-16 with a BOM, as written
    /// by Windows PowerShell 5. A leading byte-order mark is never left in the text.
    /// </summary>
    public static string Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var reader = new StreamReader(new MemoryStream(bytes), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd().Replace(((char)0xFEFF).ToString(), string.Empty, StringComparison.Ordinal);
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
        OwnSuffixes.Any(suffix => checksum.Name.Equals(asset.Name + suffix, StringComparison.OrdinalIgnoreCase));

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
