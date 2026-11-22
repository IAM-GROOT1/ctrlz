// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Services;

namespace CtrlZ.Core.Tests;

public sealed class DownloadResumeTests : IDisposable
{
    private static readonly byte[] Payload = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 31 % 251)).ToArray();
    private static readonly string PayloadHash = Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant();
    private const string Url = "https://example.invalid/setup.exe";
    private const string Mirror = "https://mirror.invalid/setup.exe";

    private readonly TestDirectory _temp = new();
    private readonly AppPaths _paths;

    public DownloadResumeTests()
    {
        _paths = new AppPaths(_temp.PathOf("data"));
        _paths.EnsureCreated();
    }

    public void Dispose() => _temp.Dispose();

    private sealed class Server(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request, Requests.Count));
        }
    }

    /// <summary>Serves bytes and then fails, like a dropped connection.</summary>
    private sealed class BreakingStream(byte[] data, int breakAfter, Func<Exception>? failure = null) : Stream
    {
        private int _position;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= breakAfter && breakAfter < data.Length)
            {
                throw failure?.Invoke() ?? new IOException("connection reset");
            }

            var n = Math.Min(count, Math.Min(data.Length, breakAfter) - _position);
            Array.Copy(data, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A stream that never returns data until cancelled.</summary>
    private sealed class HangingStream : Stream
    {
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static HttpResponseMessage Full(Stream? body = null, string? etag = "\"v1\"")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body ?? new MemoryStream(Payload)) };
        response.Content.Headers.ContentLength = Payload.Length;
        if (etag is not null)
        {
            response.Headers.ETag = new EntityTagHeaderValue(etag);
        }

        return response;
    }

    private static HttpResponseMessage Partial(long from, string etag = "\"v1\"")
    {
        var rest = Payload[(int)from..];
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(rest) };
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, Payload.Length - 1, Payload.Length);
        response.Headers.ETag = new EntityTagHeaderValue(etag);
        return response;
    }

    /// <summary>Answers a matching range request with 206, anything else with the full file.</summary>
    private static HttpResponseMessage Honest(HttpRequestMessage request)
    {
        var from = request.Headers.Range?.Ranges.First().From;
        var ifRange = request.Headers.TryGetValues("If-Range", out var values) ? values.First() : null;
        return from is > 0 && ifRange == "\"v1\"" ? Partial(from.Value) : Full();
    }

    private DownloadService Service(Server server, TimeSpan? stall = null) =>
        new(_paths, TestFactory.Log, new HttpClient(server))
        {
            RetryDelay = _ => TimeSpan.Zero,
            StallTimeout = stall ?? TimeSpan.FromSeconds(60)
        };

    private void AssertDownloaded(DownloadedFile file)
    {
        Assert.Equal(Payload, File.ReadAllBytes(file.Path));
        Assert.Equal(PayloadHash, file.Sha256);
        Assert.Empty(Directory.GetFiles(_paths.Downloads, "*.part*"));
    }

    [Fact]
    public async Task A_dropped_connection_resumes_with_a_guarded_range_request()
    {
        var server = new Server((request, n) => n == 1 ? Full(new BreakingStream(Payload, 120_000)) : Honest(request));

        var file = await Service(server).DownloadAsync(Url, "setup.exe", PayloadHash);

        AssertDownloaded(file);
        Assert.True(file.HashVerified);
        Assert.Equal(2, server.Requests.Count);
        Assert.Equal(120_000, server.Requests[1].Headers.Range!.Ranges.First().From);
        Assert.Equal("\"v1\"", server.Requests[1].Headers.GetValues("If-Range").Single());
    }

    [Fact]
    public async Task A_server_that_ignores_the_range_gets_a_clean_restart()
    {
        var server = new Server((_, n) => n == 1 ? Full(new BreakingStream(Payload, 50_000)) : Full());

        var file = await Service(server).DownloadAsync(Url, "setup.exe", PayloadHash);

        AssertDownloaded(file);
    }

    [Fact]
    public async Task Without_a_validator_nothing_is_resumed()
    {
        var server = new Server((_, n) => n == 1 ? Full(new BreakingStream(Payload, 50_000), etag: null) : Full(etag: null));

        var file = await Service(server).DownloadAsync(Url, "setup.exe", null);

        AssertDownloaded(file);
        Assert.Null(server.Requests[1].Headers.Range);
    }

    [Fact]
    public async Task A_weak_etag_is_not_used_to_resume()
    {
        var server = new Server((_, n) =>
        {
            var response = n == 1 ? Full(new BreakingStream(Payload, 50_000), etag: null) : Full(etag: null);
            response.Headers.ETag = new EntityTagHeaderValue("\"w\"", isWeak: true);
            return response;
        });

        await Service(server).DownloadAsync(Url, "setup.exe", null);

        Assert.Null(server.Requests[1].Headers.Range);
    }

    [Fact]
    public async Task A_partial_answer_for_another_range_is_thrown_away()
    {
        var server = new Server((_, n) => n switch
        {
            1 => Full(new BreakingStream(Payload, 100_000)),
            2 => Partial(90_000),
            _ => Full()
        });

        var file = await Service(server).DownloadAsync(Url, "setup.exe", PayloadHash);

        AssertDownloaded(file);
        Assert.Equal(3, server.Requests.Count);
        Assert.Null(server.Requests[2].Headers.Range);
    }

    [Fact]
    public async Task Range_not_satisfiable_starts_over()
    {
        var server = new Server((_, n) => n switch
        {
            1 => Full(new BreakingStream(Payload, 100_000)),
            2 => new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable),
            _ => Full()
        });

        AssertDownloaded(await Service(server).DownloadAsync(Url, "setup.exe", PayloadHash));
    }

    [Fact]
    public async Task A_partial_file_from_an_earlier_run_is_continued_only_for_the_same_url()
    {
        var first = new Server((_, _) => Full(new BreakingStream(Payload, 70_000)));
        await Assert.ThrowsAsync<DownloadException>(() => Service(first).DownloadAsync(Url, "setup.exe", PayloadHash));
        Assert.True(File.Exists(Path.Combine(_paths.Downloads, "setup.exe.part")));

        var other = new Server((request, _) => Honest(request));
        await Service(other).DownloadAsync(Mirror, "setup.exe", PayloadHash);
        Assert.Null(other.Requests[0].Headers.Range);

        var again = new Server((_, _) => Full(new BreakingStream(Payload, 70_000)));
        await Assert.ThrowsAsync<DownloadException>(() => Service(again).DownloadAsync(Url, "setup.exe", PayloadHash));
        var later = new Server((request, _) => Honest(request));
        AssertDownloaded(await Service(later).DownloadAsync(Url, "setup.exe", PayloadHash));
        Assert.Equal(70_000, later.Requests[0].Headers.Range!.Ranges.First().From);
    }

    [Fact]
    public async Task Server_errors_are_retried_and_missing_files_move_to_the_mirror()
    {
        var server = new Server((request, n) => n switch
        {
            1 => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            2 => new HttpResponseMessage(HttpStatusCode.NotFound),
            _ => request.RequestUri!.Host == "mirror.invalid" ? Full() : new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        var file = await Service(server).DownloadAsync([Url, Mirror], "setup.exe", PayloadHash);

        AssertDownloaded(file);
        Assert.Equal(["example.invalid", "example.invalid", "mirror.invalid"], server.Requests.Select(r => r.RequestUri!.Host));
    }

    [Fact]
    public async Task A_hash_mismatch_never_falls_through_to_a_mirror()
    {
        var server = new Server((_, _) => Full());

        await Assert.ThrowsAsync<DownloadHashMismatchException>(() =>
            Service(server).DownloadAsync([Url, Mirror], "setup.exe", new string('0', 64)));

        Assert.Single(server.Requests);
        Assert.Empty(Directory.GetFiles(_paths.Downloads));
    }

    [Fact]
    public async Task Every_url_failing_reports_the_last_failure()
    {
        var server = new Server((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<DownloadException>(() => Service(server).DownloadAsync([Url, Mirror], "setup.exe", null));

        Assert.Equal(404, ex.StatusCode);
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task A_stalled_transfer_is_retried()
    {
        var server = new Server((_, n) => n == 1 ? Full(new HangingStream()) : Full());

        AssertDownloaded(await Service(server, TimeSpan.FromMilliseconds(200)).DownloadAsync(Url, "setup.exe", PayloadHash));
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task Cancelling_removes_the_partial_file()
    {
        using var cts = new CancellationTokenSource();
        var server = new Server((_, _) => Full(new BreakingStream(Payload, 60_000, () =>
        {
            cts.Cancel();
            return new OperationCanceledException(cts.Token);
        })));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(server).DownloadAsync(Url, "setup.exe", PayloadHash, cancellationToken: cts.Token));

        Assert.Empty(Directory.GetFiles(_paths.Downloads));
    }

    [Fact]
    public async Task Invalid_urls_are_refused_before_anything_is_fetched()
    {
        var server = new Server((_, _) => Full());

        await Assert.ThrowsAsync<DownloadException>(() => Service(server).DownloadAsync([Url, "file:///c:/x.exe"], "setup.exe", null));
        Assert.Empty(server.Requests);
    }
}

