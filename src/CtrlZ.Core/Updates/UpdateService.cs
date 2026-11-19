// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Text.Json;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Updates;

/// <summary>
/// Turns the GitHub Releases payload into the newest release for a channel and decides
/// whether it is an upgrade.
/// </summary>
public sealed class UpdateService
{
    private readonly IReleaseFeed _feed;
    private readonly IAppLog _log;

    public UpdateService(IReleaseFeed feed, IAppLog log)
    {
        _feed = feed;
        _log = log;
    }

    public async Task<UpdateCheck> CheckAsync(
        SemanticVersion current,
        UpdateChannel channel,
        CancellationToken cancellationToken = default)
    {
        var json = await _feed.GetReleasesJsonAsync(cancellationToken).ConfigureAwait(false);
        var releases = ParseReleases(json);

        var candidates = releases
            .Where(release => channel == UpdateChannel.PreRelease || !release.IsPreRelease)
            .ToList();

        var latest = candidates
            .OrderByDescending(release => release.Version)
            .ThenByDescending(release => release.PublishedAt ?? DateTimeOffset.MinValue)
            .FirstOrDefault();

        var check = new UpdateCheck(channel, current, latest);

        _log.Write(
            LogLevel.Info,
            latest is null
                ? $"Update check ({channel}): no published releases."
                : $"Update check ({channel}): newest is {latest.Version}, running {current}, " +
                  $"update available: {check.UpdateAvailable}.");

        return check;
    }

    /// <summary>
    /// Parses a GitHub releases array. Drafts are ignored, entries whose tag is not a
    /// semantic version are skipped, and a malformed payload raises a friendly failure.
    /// </summary>
    public static IReadOnlyList<ReleaseInfo> ParseReleases(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new UpdateCheckException(UpdateFailureKind.Malformed, "GitHub returned an empty response.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new UpdateCheckException(UpdateFailureKind.Malformed, "GitHub returned malformed JSON.", ex);
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                // An error payload such as {"message": "API rate limit exceeded"}.
                var message = GetString(root, "message");
                throw new UpdateCheckException(
                    UpdateFailureKind.Malformed,
                    string.IsNullOrWhiteSpace(message)
                        ? "GitHub returned an unexpected response."
                        : $"GitHub returned an error: {message}");
            }

            if (root.ValueKind != JsonValueKind.Array)
            {
                throw new UpdateCheckException(UpdateFailureKind.Malformed, "GitHub returned an unexpected response.");
            }

            var releases = new List<ReleaseInfo>();
            foreach (var element in root.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                // Drafts are never offered to users.
                if (element.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
                {
                    continue;
                }

                var tag = GetString(element, "tag_name");
                if (string.IsNullOrWhiteSpace(tag) || !SemanticVersion.TryParse(tag, out var version) || version is null)
                {
                    continue;
                }

                releases.Add(new ReleaseInfo(
                    TagName: tag,
                    Version: version,
                    Title: GetString(element, "name") ?? tag,
                    Notes: GetString(element, "body"),
                    HtmlUrl: GetString(element, "html_url") ?? $"https://github.com/{GitHubReleaseFeed.Repository}/releases",
                    PublishedAt: GetTimestamp(element, "published_at"),
                    IsPreRelease: element.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True,
                    Assets: ParseAssets(element)));
            }

            if (releases.Count == 0)
            {
                throw new UpdateCheckException(
                    UpdateFailureKind.NotFound,
                    "No published releases with a semantic version tag were found.");
            }

            return releases;
        }
    }

    private static IReadOnlyList<UpdateAsset> ParseAssets(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<UpdateAsset>();
        foreach (var asset in assets.EnumerateArray())
        {
            var name = GetString(asset, "name");
            var url = GetString(asset, "browser_download_url");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            long size = 0;
            if (asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsed))
            {
                size = parsed;
            }

            result.Add(new UpdateAsset(name, url, size, ParseDigest(GetString(asset, "digest"))));
        }

        return result;
    }

    /// <summary>GitHub reports digests as <c>sha256:&lt;hex&gt;</c>; bare 64-char hex is accepted too.</summary>
    public static string? ParseDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            return null;
        }

        var value = digest.Trim();
        var colon = value.IndexOf(':');
        if (colon > 0)
        {
            var algorithm = value[..colon];
            if (!algorithm.Equals("sha256", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            value = value[(colon + 1)..];
        }

        value = value.Trim().ToLowerInvariant();
        return value.Length == 64 && value.All(IsHex) ? value : null;
    }

    private static bool IsHex(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f';

    private static DateTimeOffset? GetTimestamp(JsonElement element, string name)
    {
        var text = GetString(element, name);
        return DateTimeOffset.TryParse(text, out var parsed) ? parsed : null;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
