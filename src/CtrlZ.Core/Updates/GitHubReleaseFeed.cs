// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Services;

namespace CtrlZ.Core.Updates;

/// <summary>
/// Reads the release list from the public GitHub API.
/// <para>
/// Deliberately sends <b>no</b> Authorization header. The update check must work with no
/// secrets at all, so a token that happens to be in the environment is never forwarded —
/// unlike the installer's GitHub lookup, which does forward GITHUB_TOKEN.
/// </para>
/// </summary>
public sealed class GitHubReleaseFeed : IReleaseFeed
{
    /// <summary>The production repository. A release build can only ever use this.</summary>
    public const string ProductionRepository = "IAM-GROOT1/ctrlz";

#if DEBUG
    /// <summary>
    /// Debug-only manual-testing hook. It overrides <b>only</b> the owner/name used to build
    /// the API path; the scheme, host and path shape stay fixed in
    /// <see cref="BuildReleasesUri"/>. Release builds compile this out entirely.
    /// </summary>
    public const string RepositoryEnvironmentVariable = "CTRLZ_UPDATE_REPO";
#endif

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Repository the updater talks to. In a release build this is always
    /// <see cref="ProductionRepository"/>.
    /// </summary>
    public static string Repository => ResolveRepository();

    public static readonly Uri ReleasesUri = BuildReleasesUri(Repository);

    private readonly DownloadService _downloads;

    public GitHubReleaseFeed(DownloadService downloads)
    {
        _downloads = downloads;
    }

    public async Task<string> GetReleasesJsonAsync(CancellationToken cancellationToken = default)
    {
        var headers = new Dictionary<string, string>
        {
            ["Accept"] = "application/vnd.github+json",
            ["X-GitHub-Api-Version"] = "2022-11-28"
        };
        // Debug-only, for automated tests: the GitHub API over HTTPS may receive the test token.
        // Release builds compile the whole path out; no other host can ever see it.
        var testToken = UpdateFeedAuthorization.For(ReleasesUri);
        if (testToken is not null)
        {
            headers["Authorization"] = "Bearer " + testToken;
        }

        try
        {
            return await _downloads
                .GetStringAsync(ReleasesUri.ToString(), Timeout, cancellationToken, headers)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new UpdateCheckException(UpdateFailureKind.Offline, "The update check timed out.", ex);
        }
        catch (DownloadException ex) when (ex.StatusCode is 403 or 429)
        {
            throw new UpdateCheckException(UpdateFailureKind.RateLimited, UpdateFailureMessages.RateLimited, ex);
        }
        catch (DownloadException ex) when (ex.StatusCode == 404)
        {
            throw new UpdateCheckException(UpdateFailureKind.NotFound, ex.Message, ex);
        }
        catch (Exception ex) when (ex is DownloadException or HttpRequestException)
        {
            throw new UpdateCheckException(UpdateFailureKind.Offline, ex.Message, ex);
        }
    }

    private static string ResolveRepository()
    {
#if DEBUG
        // An invalid override is ignored rather than allowed through: a malformed value must
        // never be able to reshape the request URL.
        if (TryParseRepositoryOverride(
                Environment.GetEnvironmentVariable(RepositoryEnvironmentVariable),
                out var overridden))
        {
            return overridden;
        }
#endif
        return ProductionRepository;
    }

    /// <summary>
    /// Validates a debug override. Accepts exactly <c>owner/name</c> and nothing else — no
    /// scheme, no host, no query, no extra segments, no traversal. Always compiled so the
    /// rules are unit-testable; only the environment lookup above is DEBUG-only.
    /// </summary>
    public static bool TryParseRepositoryOverride(string? value, out string repository)
    {
        repository = ProductionRepository;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Contains("://", StringComparison.Ordinal) ||
            trimmed.Contains('\\') ||
            trimmed.Contains(':') ||
            trimmed.Contains('?') ||
            trimmed.Contains('#') ||
            trimmed.Contains('@'))
        {
            return false;
        }

        var parts = trimmed.Split('/');
        if (parts.Length != 2 || !IsValidRepositorySegment(parts[0]) || !IsValidRepositorySegment(parts[1]))
        {
            return false;
        }

        repository = parts[0] + "/" + parts[1];
        return true;
    }

    private static bool IsValidRepositorySegment(string segment)
    {
        if (segment.Length is 0 or > 100)
        {
            return false;
        }

        if (segment is "." or "..")
        {
            return false;
        }

        foreach (var character in segment)
        {
            var allowed = character is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '-' or '_' or '.';
            if (!allowed)
            {
                return false;
            }
        }

        // GitHub does not allow consecutive dots, and rejecting them also removes the only
        // path-traversal shape a naive join could produce.
        return !segment.Contains("..", StringComparison.Ordinal);
    }

    private static Uri BuildReleasesUri(string repository)
    {
        var uri = new Uri($"https://api.github.com/repos/{repository}/releases?per_page=30");

        // Defence in depth: whatever produced `repository`, the result must still be a
        // GitHub API releases URL for exactly two path segments under /repos/.
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var isExpectedShape =
            uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) &&
            segments.Length == 4 &&
            segments[0].Equals("repos", StringComparison.Ordinal) &&
            segments[3].Equals("releases", StringComparison.Ordinal);

        if (!isExpectedShape)
        {
            throw new InvalidOperationException(
                "The update feed URL is not a GitHub API releases URL.");
        }

        return uri;
    }
}
