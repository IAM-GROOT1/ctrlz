using System.Text.Json;
using System.Text.RegularExpressions;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Services;

public sealed record GitHubAsset(
    string Name,
    string DownloadUrl,
    long Size,
    string? ContentType,
    string? Sha256)
{
    public string Extension => Path.GetExtension(Name).ToLowerInvariant();
    public string SizeLabel => Infrastructure.FileUtil.FormatBytes(Size);
}

public sealed record GitHubRelease(
    string Repository,
    string TagName,
    string Title,
    string HtmlUrl,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<GitHubAsset> Assets);

public sealed partial class GitHubReleaseService
{
    private static readonly string[] UnsupportedSuffixes =
    [
        ".sha256", ".sha1", ".md5", ".sig", ".asc", ".json", ".txt", ".yml", ".yaml",
        ".blockmap", ".pdb", ".nupkg", ".snupkg", ".dmg", ".pkg", ".deb", ".rpm",
        ".appimage", ".apk", ".7z", ".tar", ".gz", ".xz", ".bz2", ".iso", ".msi.sha256"
    ];

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+$")]
    private static partial Regex RepoSegmentRegex();

    private readonly DownloadService _downloads;
    private readonly IAppLog _log;

    public GitHubReleaseService(DownloadService downloads, IAppLog log)
    {
        _downloads = downloads;
        _log = log;
    }

    public static bool TryParseRepository(string? input, out string repository, out string error)
    {
        repository = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Enter a GitHub repository, for example github.com/owner/project.";
            return false;
        }

        var value = input.Trim().TrimEnd('/');
        if (value.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
        {
            value = "https://github.com/" + value["git@github.com:".Length..];
        }

        if (!value.Contains("://", StringComparison.Ordinal) && value.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase))
        {
            value = "https://" + value;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
                !uri.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase))
            {
                error = "Only github.com repositories are supported.";
                return false;
            }

            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2)
            {
                error = "The URL does not contain an owner and repository.";
                return false;
            }

            value = $"{segments[0]}/{segments[1]}";
        }

        var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            error = "Use the form github.com/owner/project or owner/project.";
            return false;
        }

        var owner = parts[0];
        var name = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? parts[1][..^4]
            : parts[1];
        if (!RepoSegmentRegex().IsMatch(owner) || !RepoSegmentRegex().IsMatch(name))
        {
            error = "The repository owner or name contains unsupported characters.";
            return false;
        }

        repository = $"{owner}/{name}";
        return true;
    }

    public async Task<GitHubRelease> GetLatestAsync(string repository, CancellationToken cancellationToken = default)
    {
        if (!TryParseRepository(repository, out var normalized, out var error))
        {
            throw new ArgumentException(error, nameof(repository));
        }

        var url = $"https://api.github.com/repos/{normalized}/releases/latest";
        var headers = new Dictionary<string, string>
        {
            ["Accept"] = "application/vnd.github+json",
            ["X-GitHub-Api-Version"] = "2022-11-28"
        };
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            headers["Authorization"] = "Bearer " + token.Trim();
        }

        string json;
        try
        {
            json = await _downloads.GetStringAsync(url, TimeSpan.FromSeconds(30), cancellationToken, headers)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Could not reach GitHub for {normalized}. Check the repository name and network connection.", ex);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("message", out var message))
            {
                throw new InvalidOperationException($"GitHub: {message.GetString()}");
            }

            var tag = GetString(root, "tag_name") ?? "latest";
            var title = GetString(root, "name") ?? tag;
            var htmlUrl = GetString(root, "html_url") ?? $"https://github.com/{normalized}/releases";
            DateTimeOffset? published = null;
            if (root.TryGetProperty("published_at", out var publishedElement) &&
                publishedElement.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(publishedElement.GetString(), out var parsed))
            {
                published = parsed;
            }

            var assets = new List<GitHubAsset>();
            if (root.TryGetProperty("assets", out var assetsElement) && assetsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsElement.EnumerateArray())
                {
                    var name = GetString(asset, "name");
                    var downloadUrl = GetString(asset, "browser_download_url");
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(downloadUrl))
                    {
                        continue;
                    }

                    long size = asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize)
                        ? parsedSize
                        : 0;
                    var digest = GetString(asset, "digest");
                    assets.Add(new GitHubAsset(
                        name,
                        downloadUrl,
                        size,
                        GetString(asset, "content_type"),
                        ParseDigest(digest)));
                }
            }

            _log.Write(LogLevel.Info, $"GitHub {normalized}: release {tag} with {assets.Count} assets.");
            return new GitHubRelease(normalized, tag, title, htmlUrl, published, assets);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("GitHub returned an unexpected response.", ex);
        }
    }

    public IReadOnlyList<GitHubAsset> RankWindowsAssets(GitHubRelease release)
    {
        return release.Assets
            .Where(IsSupportedAsset)
            .Select(asset => (Asset: asset, Score: Score(asset)))
            .Where(pair => pair.Score > 0)
            .OrderByDescending(pair => pair.Score)
            .ThenByDescending(pair => pair.Asset.Size)
            .Select(pair => pair.Asset)
            .ToArray();
    }

    public GitHubAsset? ChooseInstallAsset(GitHubRelease release, AppEntry app)
    {
        if (!string.IsNullOrWhiteSpace(app.GitHubAsset))
        {
            var exact = release.Assets.FirstOrDefault(a =>
                a.Name.Equals(app.GitHubAsset, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }
        }

        var ranked = RankWindowsAssets(release);
        if (!string.IsNullOrWhiteSpace(app.GitHubAssetExtension))
        {
            var extensionMatch = ranked.FirstOrDefault(a =>
                a.Extension.Equals(app.GitHubAssetExtension, StringComparison.OrdinalIgnoreCase));
            if (extensionMatch is not null)
            {
                return extensionMatch;
            }
        }

        return ranked.FirstOrDefault();
    }

    public static GitHubAssetKind SuggestAssetKind(GitHubAsset asset)
    {
        return asset.Extension switch
        {
            ".zip" => GitHubAssetKind.PortableArchive,
            ".exe" => GitHubAssetKind.Installer,
            _ => GitHubAssetKind.Installer
        };
    }

    public static string? ParseDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            return null;
        }

        var index = digest.IndexOf(':');
        if (index >= 0 && digest[..index].Equals("sha256", StringComparison.OrdinalIgnoreCase))
        {
            return DownloadService.NormalizeHash(digest[(index + 1)..]);
        }

        return digest.Length == 64 ? DownloadService.NormalizeHash(digest) : null;
    }

    private static bool IsSupportedAsset(GitHubAsset asset)
    {
        var lower = asset.Name.ToLowerInvariant();
        if (lower.Contains("source code", StringComparison.Ordinal))
        {
            return false;
        }

        if (UnsupportedSuffixes.Any(suffix => lower.EndsWith(suffix, StringComparison.Ordinal)))
        {
            return false;
        }

        return asset.Extension is ".exe" or ".msi" or ".zip" or ".msix" or ".appx" or ".msixbundle";
    }

    private static int Score(GitHubAsset asset)
    {
        var name = asset.Name.ToLowerInvariant();
        var score = asset.Extension switch
        {
            ".exe" => 60,
            ".msi" => 58,
            ".zip" => 42,
            ".msix" or ".appx" or ".msixbundle" => 35,
            _ => 0
        };

        if (name.Contains("setup", StringComparison.Ordinal) ||
            name.Contains("installer", StringComparison.Ordinal) ||
            name.Contains("install", StringComparison.Ordinal))
        {
            score += 12;
        }

        if (name.Contains("x64", StringComparison.Ordinal) ||
            name.Contains("amd64", StringComparison.Ordinal) ||
            name.Contains("win64", StringComparison.Ordinal) ||
            name.Contains("windows", StringComparison.Ordinal))
        {
            score += 8;
        }

        if (name.Contains("arm64", StringComparison.Ordinal) ||
            name.Contains("aarch64", StringComparison.Ordinal))
        {
            score -= Environment.Is64BitOperatingSystem && System.Runtime.InteropServices.RuntimeInformation.OSArchitecture
                == System.Runtime.InteropServices.Architecture.Arm64 ? 0 : 60;
        }

        if (name.Contains("windows", StringComparison.Ordinal) ||
            name.Contains("win", StringComparison.Ordinal) ||
            name.Contains("win32", StringComparison.Ordinal))
        {
            score += 5;
        }

        if (name.Contains("mac", StringComparison.Ordinal) ||
            name.Contains("darwin", StringComparison.Ordinal) ||
            name.Contains("linux", StringComparison.Ordinal) ||
            name.Contains("symbols", StringComparison.Ordinal) ||
            name.Contains("debug", StringComparison.Ordinal))
        {
            score -= 80;
        }

        if (name.Contains("portable", StringComparison.Ordinal))
        {
            score += 3;
        }

        return score;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
