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
    public const string Repository = "IAM-GROOT1/ctrlz";

    /// <summary>Thirty releases is enough to cover both channels without paginating.</summary>
    public static readonly Uri ReleasesUri =
        new($"https://api.github.com/repos/{Repository}/releases?per_page=30");

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

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
            throw new UpdateCheckException(UpdateFailureKind.RateLimited, ex.Message, ex);
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
}
