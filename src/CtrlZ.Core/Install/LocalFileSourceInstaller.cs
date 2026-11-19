// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Models;
using CtrlZ.Core.Services;

namespace CtrlZ.Core.Install;

public sealed class LocalFileSourceInstaller : ISourceInstaller
{
    private readonly FileInstallService _files;

    public LocalFileSourceInstaller(FileInstallService files)
    {
        _files = files;
    }

    public bool CanHandle(AppSource source) => source == AppSource.LocalFile;

    public async Task<InstallResult> InstallAsync(AppEntry app, InstallContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(app.LocalPath))
        {
            return InstallResult.NeedsAction("No local installer path is configured.");
        }

        if (!File.Exists(app.LocalPath))
        {
            return InstallResult.NeedsAction(
                $"The installer was not found at {app.LocalPath}. Reconnect the USB drive or update the path.");
        }

        if (!string.IsNullOrWhiteSpace(app.ExpectedSha256))
        {
            var actual = await FileUtil.Sha256Async(app.LocalPath, cancellationToken).ConfigureAwait(false);
            if (!actual.Equals(DownloadService.NormalizeHash(app.ExpectedSha256), StringComparison.OrdinalIgnoreCase))
            {
                context.Report(app, InstallStatus.Failed, "SHA-256 verification failed.");
                return InstallResult.Fail($"Local installer hash mismatch. Expected {app.ExpectedSha256}, got {actual}.");
            }

            context.Line("Local installer SHA-256 verified.");
        }

        var extension = Path.GetExtension(app.LocalPath);
        var kind = extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
            ? GitHubAssetKind.PortableArchive
            : GitHubAssetKind.Installer;
        return await _files.InstallAsync(app, app.LocalPath, kind, context, cancellationToken).ConfigureAwait(false);
    }
}
