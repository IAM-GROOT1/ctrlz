// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Services;

namespace CtrlZ.Core.Tests;

/// <summary>"Refresh the app catalog automatically" off: the Catalog page never goes online by itself.</summary>
public class CatalogRefreshSettingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ctrlz-catalog-" + Guid.NewGuid().ToString("N"));

    private sealed class QuietLog : IAppLog
    {
        public string LogFilePath => string.Empty;
        public string LogsFolder => string.Empty;
        public event EventHandler<LogLine>? LineWritten { add { } remove { } }
        public void Write(LogLevel level, string message, Exception? exception = null) { }
        public void Dispose() { }
    }

    /// <summary>Counts requests and fails each one, like being offline.</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            throw new HttpRequestException("offline");
        }
    }

    private (WinUtilCatalogService Service, CountingHandler Handler, AppPaths Paths) Create()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        var handler = new CountingHandler();
        var log = new QuietLog();
        return (new WinUtilCatalogService(paths, new DownloadService(paths, log, new HttpClient(handler)), log), handler, paths);
    }

    [Fact]
    public async Task With_automatic_refresh_off_nothing_is_downloaded()
    {
        var (service, handler, _) = Create();

        var catalog = await service.LoadAsync(forceRefresh: false, autoRefresh: false);

        Assert.Equal(0, handler.Requests);
        Assert.True(catalog.IsBundledFallback);
        Assert.NotEmpty(catalog.Items);
        Assert.Null(catalog.Warning);
        Assert.Equal("Built-in copy", catalog.DisplayStatus);
        Assert.Equal("Bundled snapshot", catalog.StatusText);
    }

    [Fact]
    public async Task With_automatic_refresh_off_an_old_saved_copy_is_still_used()
    {
        var (service, handler, paths) = Create();
        File.Copy(BundledJsonPath(), paths.CatalogCacheFile);
        File.WriteAllText(paths.CatalogMetadataFile, "{ \"fetchedUtc\": \"2020-01-01T00:00:00+00:00\" }");

        var catalog = await service.LoadAsync(forceRefresh: false, autoRefresh: false);

        Assert.Equal(0, handler.Requests);
        Assert.True(catalog.FromCache);
        Assert.False(catalog.IsBundledFallback);
        Assert.StartsWith("Saved copy from ", catalog.DisplayStatus);
    }

    [Fact]
    public async Task Refresh_still_goes_online_and_explains_a_failure()
    {
        var (service, handler, _) = Create();

        var catalog = await service.LoadAsync(forceRefresh: true, autoRefresh: false);

        Assert.True(handler.Requests > 0);
        Assert.True(catalog.IsBundledFallback);
        Assert.Equal("You are offline or GitHub is unavailable. Showing the catalog built into CTRL+Z.", catalog.Warning);
    }

    [Fact]
    public async Task With_automatic_refresh_on_a_missing_copy_is_downloaded()
    {
        var (service, handler, _) = Create();

        await service.LoadAsync(forceRefresh: false, autoRefresh: true);

        Assert.True(handler.Requests > 0);
    }

    /// <summary>Writes the embedded snapshot to a file so it can stand in for a saved copy.</summary>
    private string BundledJsonPath()
    {
        var assembly = typeof(WinUtilCatalogService).Assembly;
        using var stream = assembly.GetManifestResourceStream("CtrlZ.Core.Resources.winutil-applications.json")!;
        var path = Path.Combine(_root, "snapshot.json");
        using var file = File.Create(path);
        stream.CopyTo(file);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // A temp folder left behind is harmless.
        }
    }
}
