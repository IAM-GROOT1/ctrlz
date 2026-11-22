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
    public DownloadException(string message, Exception? inner = null, int? statusCode = null) : base(message, inner)
    {
        StatusCode = statusCode;
    }

    /// <summary>HTTP status code when the failure came from a response, otherwise null.</summary>
    public int? StatusCode { get; }
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
    private readonly HttpClient _client;

    public DownloadService(AppPaths paths, IAppLog log)
        : this(paths, log, Client)
    {
    }

    /// <summary>For tests: every request goes through <paramref name="client"/> instead of the network.</summary>
    public DownloadService(AppPaths paths, IAppLog log, HttpClient client)
    {
        _paths = paths;
        _log = log;
        _client = client;
    }

    /// <summary>Attempts per URL before moving to the next mirror. A failure mid-file resumes.</summary>
    public const int MaxAttemptsPerUrl = 3;

    /// <summary>A transfer that receives nothing for this long is treated as failed and retried.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Wait before retry n (1-based). Replaceable so tests do not sleep.</summary>
    public Func<int, TimeSpan> RetryDelay { get; init; } = attempt => TimeSpan.FromSeconds(attempt * 2);

    public Task<DownloadedFile> DownloadAsync(
        string url,
        string? suggestedFileName,
        string? expectedSha256,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        DownloadAsync([url], suggestedFileName, expectedSha256, progress, cancellationToken);

    /// <summary>
    /// Downloads from the first URL that works, trying the next one (a mirror) only when a URL
    /// cannot be fetched. A failure part-way through resumes from where it stopped, using an HTTP
    /// range request guarded by If-Range, so a file that changed on the server starts over instead
    /// of being stitched together. A partial file and its validator are kept on disk, so a later
    /// call for the same URL and file name continues it. The SHA-256, when given, is checked over
    /// the whole file, and a mismatch never falls through to another mirror.
    /// </summary>
    public async Task<DownloadedFile> DownloadAsync(
        IReadOnlyList<string> urls,
        string? suggestedFileName,
        string? expectedSha256,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(urls);
        var uris = new List<Uri>();
        foreach (var url in urls)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                throw new DownloadException("The download URL must be an absolute http(s) URL.");
            }

            uris.Add(uri);
        }

        if (uris.Count == 0)
        {
            throw new DownloadException("No download URL was given.");
        }

        Directory.CreateDirectory(_paths.Downloads);
        DownloadException? lastFailure = null;
        for (var index = 0; index < uris.Count; index++)
        {
            if (index > 0)
            {
                _log.Write(LogLevel.Warning, $"Trying mirror {uris[index].Host} after: {lastFailure?.Message}");
            }

            try
            {
                return await DownloadOneAsync(uris[index], suggestedFileName, expectedSha256, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (DownloadException ex)
            {
                lastFailure = ex;
            }
        }

        throw lastFailure!;
    }

    private async Task<DownloadedFile> DownloadOneAsync(
        Uri uri,
        string? suggestedFileName,
        string? expectedSha256,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        _log.Write(LogLevel.Info, $"Downloading {uri.Host}{uri.AbsolutePath}");
        string? fileName = suggestedFileName is null ? null : FileUtil.SafeFileName(suggestedFileName, fallback: "download.bin");
        var name = new NameHolder { Value = fileName };
        try
        {
            return await DownloadAttemptsAsync(uri, name, expectedSha256, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A cancel the user asked for removes the partial file, whichever step it interrupted.
            if (name.Value is not null)
            {
                PartialDownload.Delete(PartPath(name.Value));
            }

            throw;
        }
    }

    /// <summary>The file name, which may only become known from the server's response.</summary>
    private sealed class NameHolder
    {
        public string? Value { get; set; }
    }

    private async Task<DownloadedFile> DownloadAttemptsAsync(
        Uri uri,
        NameHolder name,
        string? expectedSha256,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var fileName = name.Value;
        DownloadException? failure = null;
        var resumeAllowed = true;
        var freeRestart = true;
        var retriedFromZero = false;

        for (var attempt = 1; attempt <= MaxAttemptsPerUrl; attempt++)
        {
            if (attempt > 1)
            {
                await Task.Delay(RetryDelay(attempt - 1), cancellationToken).ConfigureAwait(false);
            }

            var resume = fileName is null || !resumeAllowed ? null : PartialDownload.TryLoad(PartPath(fileName), uri);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (resume is not null)
            {
                request.Headers.Range = new RangeHeaderValue(resume.Length, null);
                request.Headers.TryAddWithoutValidation("If-Range", resume.Validator);
            }

            HttpResponseMessage response;
            try
            {
                response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransient(ex, cancellationToken))
            {
                failure = new DownloadException("The download could not start: " + ex.Message, ex);
                continue;
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && fileName is not null)
                {
                    PartialDownload.Delete(PartPath(fileName));
                    failure = new DownloadException("The partial download no longer matches the server's file.");
                    attempt -= FreeRestart(ref freeRestart);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var status = (int)response.StatusCode;
                    failure = new DownloadException(
                        $"Download failed ({status} {response.ReasonPhrase}). " +
                        "The link may be private, expired, or rate-limited.", statusCode: status);
                    if (status is 408 or 429 or >= 500)
                    {
                        continue;
                    }

                    throw failure;
                }

                fileName ??= FileUtil.SafeFileName(FileNameFromResponse(response, uri), fallback: "download.bin");
                if (string.IsNullOrWhiteSpace(Path.GetExtension(fileName)))
                {
                    fileName += ExtensionFromContentType(response.Content.Headers.ContentType?.MediaType);
                }

                name.Value = fileName;

                var targetPath = Path.Combine(_paths.Downloads, fileName);
                var partPath = PartPath(fileName);
                var offset = ResumeOffset(response, resume);
                if (offset < 0)
                {
                    PartialDownload.Delete(partPath);
                    failure = new DownloadException("The server's answer to the resume request did not match the saved part.");
                    attempt -= FreeRestart(ref freeRestart);
                    continue;
                }

                if (offset == 0 && resume is not null)
                {
                    _log.Write(LogLevel.Info, "The server sent the whole file; starting the download again.");
                }
                else if (offset > 0)
                {
                    _log.Write(LogLevel.Info, $"Resuming {fileName} at {FileUtil.FormatBytes(offset)}.");
                }

                long? totalBytes = response.StatusCode == HttpStatusCode.PartialContent
                    ? response.Content.Headers.ContentRange?.Length
                    : response.Content.Headers.ContentLength;
                long downloaded;
                try
                {
                    downloaded = await CopyAsync(response, uri, partPath, offset, totalBytes, progress, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (IsTransient(ex, cancellationToken))
                {
                    failure = new DownloadException("The download was interrupted: " + ex.Message, ex);
                    continue;
                }

                if (totalBytes is > 0 && downloaded != totalBytes)
                {
                    failure = new DownloadException($"Download was incomplete ({downloaded} of {totalBytes} bytes).");
                    continue;
                }

                File.Move(partPath, targetPath, overwrite: true);
                PartialDownload.Delete(partPath);
                progress?.Report(new DownloadProgress(downloaded, totalBytes, 1));
                _log.Write(LogLevel.Info, $"Downloaded {fileName} ({FileUtil.FormatBytes(downloaded)}).");

                var actualHash = await FileUtil.Sha256Async(targetPath, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(expectedSha256))
                {
                    var expected = NormalizeHash(expectedSha256);
                    if (!expected.Equals(actualHash, StringComparison.OrdinalIgnoreCase))
                    {
                        TryDelete(targetPath);
                        if (offset > 0 && !retriedFromZero)
                        {
                            // The kept part may have been bad; one clean download from byte zero decides.
                            _log.Write(LogLevel.Warning, $"{fileName} failed its SHA-256 after resuming; downloading it again from the start.");
                            retriedFromZero = true;
                            resumeAllowed = false;
                            attempt--;
                            continue;
                        }

                        throw new DownloadHashMismatchException(expected, actualHash);
                    }

                    _log.Write(LogLevel.Info, $"SHA-256 verified for {fileName}.");
                }

                return new DownloadedFile(targetPath, fileName, downloaded, actualHash,
                    HashVerified: !string.IsNullOrWhiteSpace(expectedSha256));
            }
        }

        throw failure ?? new DownloadException("The download failed.");
    }

    /// <summary>1 the first time (the restart does not use up an attempt), then 0.</summary>
    private static int FreeRestart(ref bool available)
    {
        if (!available)
        {
            return 0;
        }

        available = false;
        return 1;
    }

    private string PartPath(string fileName) => Path.Combine(_paths.Downloads, fileName + ".part");

    /// <summary>
    /// Where to continue: the saved length when the server honoured the range, 0 for a full response,
    /// and -1 when a partial response does not start where the saved file ends.
    /// </summary>
    private static long ResumeOffset(HttpResponseMessage response, PartialDownload? resume)
    {
        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            return 0;
        }

        // Appended only when it is exactly the rest of the same file: the range we asked for, to the
        // end, with a known size, and the same validator as the saved part. That last check does not
        // trust the server to honour If-Range; some ignore it and send the new file's tail.
        var range = response.Content.Headers.ContentRange;
        if (resume is null ||
            range is null ||
            !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase) ||
            range.From != resume.Length ||
            range.Length is not { } total ||
            range.To != total - 1 ||
            response.Content.Headers.ContentLength != total - resume.Length ||
            response.Content.Headers.ContentEncoding.Count > 0 ||
            Validator(response) != resume.Validator)
        {
            return -1;
        }

        return resume.Length;
    }

    /// <summary>
    /// A strong ETag, else Last-Modified; null when the response cannot be resumed safely. A response
    /// with no Content-Length is never resumable: automatic decompression removes that header (and
    /// Content-Encoding), so this also keeps compressed transfers from being resumed by byte offset.
    /// </summary>
    private static string? Validator(HttpResponseMessage response)
    {
        if (response.Content.Headers.ContentEncoding.Count > 0 || response.Content.Headers.ContentLength is null)
        {
            return null;
        }

        if (response.Headers.ETag is { IsWeak: false } etag)
        {
            return etag.Tag;
        }

        return response.Content.Headers.LastModified is { } modified ? modified.ToString("R") : null;
    }

    private async Task<long> CopyAsync(
        HttpResponseMessage response,
        Uri uri,
        string partPath,
        long offset,
        long? totalBytes,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (offset == 0)
        {
            // The old record goes first: old bytes must never sit next to a new validator.
            PartialDownload.DeleteRecord(partPath);
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(partPath, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write,
            FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (offset == 0)
        {
            target.SetLength(0);
            target.Flush(flushToDisk: true);
            PartialDownload.Save(partPath, uri, Validator(response));
        }

        var buffer = new byte[81920];
        var downloaded = offset;
        var lastReport = DateTime.UtcNow;
        while (true)
        {
            stall.CancelAfter(StallTimeout);
            int read;
            try
            {
                read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"No data was received for {StallTimeout.TotalSeconds:0} seconds.");
            }

            if (read == 0)
            {
                return downloaded;
            }

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

    /// <summary>Network trouble worth another attempt; a cancel the caller asked for is not.</summary>
    private static bool IsTransient(Exception ex, CancellationToken cancellationToken) => ex switch
    {
        HttpRequestException or IOException or TimeoutException => true,
        OperationCanceledException => !cancellationToken.IsCancellationRequested,
        _ => false
    };

    /// <summary>
    /// Sends a HEAD request so a preview or dry run can confirm a download exists and learn
    /// its size without transferring it. Returns the declared length when the server sends one.
    /// </summary>
    public async Task<long?> HeadAsync(string url, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new DownloadException("The URL must be an absolute http(s) URL.");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        using var request = new HttpRequestMessage(HttpMethod.Head, uri);
        using var response = await _client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new DownloadException(
                $"Request failed ({(int)response.StatusCode} {response.ReasonPhrase}).",
                statusCode: (int)response.StatusCode);
        }

        return response.Content.Headers.ContentLength;
    }

    public async Task<byte[]> GetBytesAsync(string url, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        using var response = await _client.GetAsync(url, cts.Token).ConfigureAwait(false);
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

        using var response = await _client.SendAsync(request, cts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Forbidden &&
                response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) &&
                remaining.FirstOrDefault() == "0")
            {
                throw new DownloadException("GitHub's anonymous API limit was reached. Try again later or set GITHUB_TOKEN.",
                    statusCode: (int)response.StatusCode);
            }

            throw new DownloadException($"Request failed ({(int)response.StatusCode} {response.ReasonPhrase}).",
                statusCode: (int)response.StatusCode);
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

/// <summary>
/// What is needed to continue a download later: the URL it came from and the server's validator
/// (a strong ETag or Last-Modified), kept in <c>name.part.meta</c> beside <c>name.part</c>. Without a
/// validator a partial file is never continued, because there would be no way to know it still
/// belongs to the file on the server.
/// </summary>
internal sealed record PartialDownload(string Url, string Validator, long Length)
{
    private sealed record Meta(string Url, string Validator);

    private static string MetaPath(string partPath) => partPath + ".meta";

    public static PartialDownload? TryLoad(string partPath, Uri uri)
    {
        try
        {
            var info = new FileInfo(partPath);
            var metaPath = MetaPath(partPath);
            if (!info.Exists || info.Length == 0 || !File.Exists(metaPath))
            {
                return null;
            }

            var meta = System.Text.Json.JsonSerializer.Deserialize<Meta>(File.ReadAllText(metaPath));
            return meta is null || string.IsNullOrWhiteSpace(meta.Validator) || meta.Url != uri.AbsoluteUri
                ? null
                : new PartialDownload(meta.Url, meta.Validator, info.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public static void Save(string partPath, Uri uri, string? validator)
    {
        try
        {
            if (validator is null)
            {
                File.Delete(MetaPath(partPath));
                return;
            }

            File.WriteAllText(MetaPath(partPath), System.Text.Json.JsonSerializer.Serialize(new Meta(uri.AbsoluteUri, validator)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the record the download still works; it just cannot be continued later.
        }
    }

    public static void DeleteRecord(string partPath)
    {
        try { if (File.Exists(MetaPath(partPath))) File.Delete(MetaPath(partPath)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static void Delete(string partPath)
    {
        foreach (var path in new[] { partPath, MetaPath(partPath) })
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
