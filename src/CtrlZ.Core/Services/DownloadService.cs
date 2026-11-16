// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Net;
using System.Net.Http.Headers;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Logging;

namespace CtrlZ.Core.Services;

public sealed record DownloadProgress(long DownloadedBytes, long? TotalBytes, double? Fraction);

public sealed class DownloadException : Exception
{
    public DownloadException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class DownloadHashMismatchException : Exception
{
    public DownloadHashMismatchException(string expected, string actual)
        : base($"Download hash mismatch. Expected {expected}, got {actual}.")
    {
        Expected = expected;
        Actual = actual;
    }

    public string Expected { get; }
    public string Actual { get; }
}

public sealed class DownloadService
{
    private static readonly HttpClient Client = CreateClient();
    private readonly AppPaths _paths;
    private readonly IAppLog _log;

    public DownloadService(AppPaths paths, IAppLog log)
    {
        _paths = paths;
        _log = log;
    }

    public async Task<DownloadedFile> DownloadAsync(
        string url,
        string? suggestedFileName,
        string? expectedSha256,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new DownloadException("The download URL must be an absolute http(s) URL.");
        }

        Directory.CreateDirectory(_paths.Downloads);
        _log.Write(LogLevel.Info, $"Downloading {uri.Host}{uri.AbsolutePath}");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new DownloadException(
                $"Download failed ({(int)response.StatusCode} {response.ReasonPhrase}). " +
                "The link may be private, expired, or rate-limited.");
        }

        var fileName = FileUtil.SafeFileName(
            suggestedFileName ?? FileNameFromResponse(response, uri),
            fallback: "download.bin");
        if (string.IsNullOrWhiteSpace(Path.GetExtension(fileName)))
        {
            fileName += ExtensionFromContentType(response.Content.Headers.ContentType?.MediaType);
        }

        var targetPath = Path.Combine(_paths.Downloads, fileName);
        var tempPath = targetPath + ".part";
        var totalBytes = response.Content.Headers.ContentLength;
        long downloaded = 0;

        try
        {
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var target = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                             81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[81920];
                var lastReport = DateTime.UtcNow;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    downloaded += read;
                    if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(120))
                    {
                        lastReport = DateTime.UtcNow;
                        double? fraction = totalBytes is > 0 ? (double)downloaded / totalBytes.Value : null;
                        progress?.Report(new DownloadProgress(downloaded, totalBytes, fraction));
                    }
                }
            }

            if (totalBytes is > 0 && downloaded != totalBytes)
            {
                throw new DownloadException($"Download was incomplete ({downloaded} of {totalBytes} bytes).");
            }

            File.Move(tempPath, targetPath, overwrite: true);
            progress?.Report(new DownloadProgress(downloaded, totalBytes, 1));
            _log.Write(LogLevel.Info, $"Downloaded {fileName} ({FileUtil.FormatBytes(downloaded)}).");

            var actualHash = await FileUtil.Sha256Async(targetPath, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(expectedSha256))
            {
                var expected = NormalizeHash(expectedSha256);
                if (!expected.Equals(actualHash, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(targetPath);
                    throw new DownloadHashMismatchException(expected, actualHash);
                }

                _log.Write(LogLevel.Info, $"SHA-256 verified for {fileName}.");
            }

            return new DownloadedFile(targetPath, fileName, downloaded, actualHash,
                HashVerified: !string.IsNullOrWhiteSpace(expectedSha256));
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempPath);
            throw;
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    public async Task<byte[]> GetBytesAsync(string url, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        using var response = await Client.GetAsync(url, cts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new DownloadException($"Request failed ({(int)response.StatusCode} {response.ReasonPhrase}).");
        }

        return await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
    }

    public async Task<string> GetStringAsync(
        string url,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (headers is not null)
        {
            foreach (var pair in headers)
            {
                request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
            }
        }

        using var response = await Client.SendAsync(request, cts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Forbidden &&
                response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) &&
                remaining.FirstOrDefault() == "0")
            {
                throw new DownloadException("GitHub's anonymous API limit was reached. Try again later or set GITHUB_TOKEN.");
            }

            throw new DownloadException($"Request failed ({(int)response.StatusCode} {response.ReasonPhrase}).");
        }

        return await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
    }

    public static string NormalizeHash(string hash) =>
        hash.Trim().Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

    private static string FileNameFromResponse(HttpResponseMessage response, Uri uri)
    {
        if (response.Content.Headers.ContentDisposition is { } disposition)
        {
            var name = disposition.FileNameStar ?? disposition.FileName;
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name.Trim('"');
            }
        }

        var segment = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return string.IsNullOrWhiteSpace(segment) ? "download.bin" : Uri.UnescapeDataString(segment);
    }

    private static string ExtensionFromContentType(string? mediaType) => mediaType switch
    {
        "application/zip" => ".zip",
        "application/x-msdownload" => ".exe",
        "application/x-msi" or "application/x-ms-installer" => ".msi",
        "application/octet-stream" => ".exe",
        _ => ".bin"
    };

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };
        var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CTRLplusZ/1.0 (+https://github.com/)");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        return client;
    }
}

public sealed record DownloadedFile(string Path, string FileName, long Size, string Sha256, bool HashVerified);
