// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Logging;
using CtrlZ.Core.Services;

namespace CtrlZ.Core.Updates;

public sealed class UpdateDownloader
{
    private static readonly TimeSpan ChecksumsTimeout = TimeSpan.FromSeconds(20);

    private readonly DownloadService _downloads;
    private readonly IAppLog _log;

    public UpdateDownloader(DownloadService downloads, IAppLog log)
    {
        _downloads = downloads;
        _log = log;
    }

    /// <summary>Downloads SHA256SUMS.txt when the release publishes one; null otherwise.</summary>
    public async Task<string?> TryGetChecksumsAsync(ReleaseInfo release, CancellationToken cancellationToken = default)
    {
        var asset = UpdateAssetSelector.FindChecksumsAsset(release);
        if (asset is null)
        {
            return null;
        }

        if (!UpdateAssetSelector.IsAllowedDownloadUrl(asset.DownloadUrl, out var error))
        {
            _log.Write(LogLevel.Warning, $"Ignoring SHA256SUMS.txt: {error}");
            return null;
        }

        try
        {
            return await _downloads
                .GetStringAsync(asset.DownloadUrl, ChecksumsTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Write(LogLevel.Warning, "Could not read SHA256SUMS.txt; falling back to the API digest.", ex);
            return null;
        }
    }

    /// <summary>
    /// Resolves the hash the download must match and refuses to continue when the release
    /// publishes nothing to verify against.
    /// </summary>
    public async Task<string> ResolveExpectedHashAsync(
        ReleaseInfo release,
        UpdateAsset asset,
        CancellationToken cancellationToken = default)
    {
        var checksums = await TryGetChecksumsAsync(release, cancellationToken).ConfigureAwait(false);
        var expected = UpdateVerifier.Resolve(checksums, asset.Name, asset.Sha256);

        if (expected is null)
        {
            throw new UpdateVerificationException(
                $"Release {release.Version} does not publish a SHA-256 for '{asset.Name}', " +
                "so CTRL+Z will not install it.");
        }

        return expected;
    }

    /// <summary>
    /// Downloads with progress and verifies the declared size and SHA-256. On any mismatch
    /// the file is deleted and an <see cref="UpdateVerificationException"/> is raised.
    /// </summary>
    public async Task<DownloadedFile> DownloadAsync(
        ReleaseInfo release,
        UpdateAsset asset,
        string expectedSha256,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        DownloadedFile file;
        try
        {
            file = await _downloads
                .DownloadAsync(asset.DownloadUrl, asset.Name, expectedSha256, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DownloadHashMismatchException ex)
        {
            _log.Write(LogLevel.Error, $"Update download failed verification for {asset.Name}.", ex);
            throw new UpdateVerificationException(
                "The downloaded update failed its SHA-256 check. It was deleted and nothing was applied.", ex);
        }
        catch (DownloadException ex)
        {
            _log.Write(LogLevel.Error, $"Update download failed for {asset.Name}.", ex);
            throw new UpdateCheckException(UpdateFailureKind.Offline, ex.Message, ex);
        }

        try
        {
            UpdateVerifier.EnsureSize(file.Size, asset.Size);
            if (!file.HashVerified)
            {
                UpdateVerifier.EnsureHash(expectedSha256, file.Sha256);
            }
        }
        catch
        {
            TryDelete(file.Path);
            throw;
        }

        _log.Write(LogLevel.Info, $"Update {release.Version} downloaded and verified ({file.Size} bytes).");
        return file;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort: the caller already has the real error.
        }
    }
}
