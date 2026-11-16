// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Models;
using CtrlZ.Core.Services;

namespace CtrlZ.Core.Install;

public sealed class DirectUrlSourceInstaller : ISourceInstaller
{
    private readonly DownloadService _downloads;
    private readonly FileInstallService _files;

    public DirectUrlSourceInstaller(DownloadService downloads, FileInstallService files)
    {
        _downloads = downloads;
        _files = files;
    }

    public bool CanHandle(AppSource source) => source == AppSource.DirectUrl;

    public async Task<InstallResult> InstallAsync(AppEntry app, InstallContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(app.DownloadUrl))
        {
            return InstallResult.NeedsAction("This app has no download URL. Edit it in My Apps.");
        }

        try
        {
            context.Report(app, InstallStatus.Downloading, "Downloading installer…", 0);
            var download = await _downloads.DownloadAsync(
                app.DownloadUrl,
                app.FileName,
                app.ExpectedSha256,
                new Progress<DownloadProgress>(p =>
                    context.Report(app, InstallStatus.Downloading, "Downloading installer…", p.Fraction)),
                cancellationToken).ConfigureAwait(false);

            var extension = Path.GetExtension(download.Path);
            var kind = extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
                ? GitHubAssetKind.PortableArchive
                : GitHubAssetKind.Installer;
            return await _files.InstallAsync(app, download.Path, kind, context, cancellationToken).ConfigureAwait(false);
        }
        catch (DownloadHashMismatchException ex)
        {
            context.Report(app, InstallStatus.Failed, "SHA-256 verification failed.");
            return InstallResult.Fail(ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            context.Report(app, InstallStatus.Failed, ex.Message);
            return InstallResult.Fail($"Download failed: {ex.Message}");
        }
    }
}
