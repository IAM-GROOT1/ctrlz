// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

namespace CtrlZ.Core.Updates;

/// <summary>Which artifact an update must download for the copy that is running.</summary>
public enum UpdateInstallKind
{
    /// <summary>Installed by the Inno Setup installer: update with the new Setup.exe.</summary>
    Installed,

    /// <summary>Standalone single exe: update by replacing the exe.</summary>
    Portable
}

public sealed record UpdateAssetSelection(UpdateAsset Asset, string ExpectedName);

/// <summary>
/// Chooses the one correct asset for a release. Selection is by exact name, because the
/// build produces <c>CTRL+Z-Setup-&lt;version&gt;.exe</c> and
/// <c>CTRL+Z-Portable-&lt;version&gt;.exe</c>; nothing is guessed by heuristic.
/// </summary>
public static class UpdateAssetSelector
{
    public const string SetupPrefix = "CTRL+Z-Setup-";
    public const string PortablePrefix = "CTRL+Z-Portable-";
    public const string ChecksumsFileName = "SHA256SUMS.txt";

    /// <summary>Hosts a GitHub release asset may legitimately redirect to.</summary>
    private static readonly string[] AllowedHosts =
    [
        "github.com",
        "objects.githubusercontent.com",
        "github-releases.githubusercontent.com",
        "release-assets.githubusercontent.com"
    ];

    private static readonly string[] ForeignArchitectureTokens = ["arm64", "aarch64", "x86", "win32", "arm"];

    public static string SetupName(SemanticVersion version) => $"{SetupPrefix}{version}.exe";

    public static string PortableName(SemanticVersion version) => $"{PortablePrefix}{version}.exe";

    public static string ExpectedName(UpdateInstallKind kind, SemanticVersion version) =>
        kind == UpdateInstallKind.Installed ? SetupName(version) : PortableName(version);

    public static bool TrySelect(
        ReleaseInfo release,
        UpdateInstallKind kind,
        out UpdateAssetSelection? selection,
        out string error) =>
        TrySelectByName(release, ExpectedName(kind, release.Version), out selection, out error);

    public const string CliPrefix = "ctrlz-";

    /// <summary>File name of the command line's release asset, <c>ctrlz-&lt;version&gt;.exe</c>.</summary>
    public static string CliName(SemanticVersion version) => $"{CliPrefix}{version}.exe";

    /// <summary>
    /// The command line asset of this exact release, with the same checks as the app's own asset.
    /// Used to update a portable <c>ctrlz.exe</c> that sits next to the portable app.
    /// </summary>
    public static bool TrySelectCli(ReleaseInfo release, out UpdateAssetSelection? selection, out string error) =>
        TrySelectByName(release, CliName(release.Version), out selection, out error);

    private static bool TrySelectByName(
        ReleaseInfo release,
        string expected,
        out UpdateAssetSelection? selection,
        out string error)
    {
        selection = null;
        error = string.Empty;

        var asset = release.Assets.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, expected, StringComparison.OrdinalIgnoreCase));

        if (asset is null)
        {
            error = DescribeMissing(release, expected);
            return false;
        }

        if (!asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            error = $"The release asset '{asset.Name}' is not an executable.";
            return false;
        }

        if (HasForeignArchitecture(asset.Name))
        {
            error = $"The release asset '{asset.Name}' is not an x64 build.";
            return false;
        }

        if (asset.Size <= 0)
        {
            error = $"The release did not declare a size for '{asset.Name}', so it cannot be verified.";
            return false;
        }

        if (!IsAllowedDownloadUrl(asset.DownloadUrl, out error))
        {
            return false;
        }

        selection = new UpdateAssetSelection(asset, expected);
        return true;
    }

    public static UpdateAsset? FindChecksumsAsset(ReleaseInfo release) =>
        release.Assets.FirstOrDefault(asset =>
            string.Equals(asset.Name, ChecksumsFileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// HTTPS only, and only GitHub's own hosts. Anything else is refused so a tampered
    /// release cannot point the updater at an arbitrary server.
    /// </summary>
    public static bool IsAllowedDownloadUrl(string? url, out string error)
    {
        error = string.Empty;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            error = "The release asset does not have a valid download URL.";
            return false;
        }

        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = "The release asset is not served over HTTPS.";
            return false;
        }

        var host = uri.Host;
        var allowed = AllowedHosts.Any(candidate =>
            host.Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + candidate, StringComparison.OrdinalIgnoreCase));

        if (!allowed)
        {
            error = $"The release asset is hosted on '{host}', which is not a GitHub download host.";
            return false;
        }

        return true;
    }

    public static bool HasForeignArchitecture(string name)
    {
        var lower = name.ToLowerInvariant();
        foreach (var token in ForeignArchitectureTokens)
        {
            if (lower.Contains(token, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string DescribeMissing(ReleaseInfo release, string expected)
    {
        var prefix = expected.StartsWith(SetupPrefix, StringComparison.OrdinalIgnoreCase)
            ? SetupPrefix
            : PortablePrefix;

        var nearMatches = release.Assets
            .Where(asset => asset.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (nearMatches.Count > 0)
        {
            var listed = string.Join(", ", nearMatches.Select(asset => asset.Name));
            if (nearMatches.Any(asset => HasForeignArchitecture(asset.Name)))
            {
                return $"Release {release.Version} only publishes a non-x64 build ({listed}). " +
                       "CTRL+Z requires the x64 build.";
            }

            return $"Release {release.Version} does not contain '{expected}'. It contains: {listed}.";
        }

        return $"Release {release.Version} does not contain '{expected}'.";
    }
}
