// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Localization;
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
            return InstallResult.NeedsAction(CoreText.Get("LocalFileSourceInstaller_NoLocalInstallerPathIs"));
        }

        if (!File.Exists(app.LocalPath))
        {
            return InstallResult.NeedsAction(
                CoreText.Format("LocalFileSourceInstaller_TheInstallerWasNotFound", app.LocalPath));
        }

        if (!string.IsNullOrWhiteSpace(app.ExpectedSha256))
        {
            var actual = await FileUtil.Sha256Async(app.LocalPath, cancellationToken).ConfigureAwait(false);
            if (!actual.Equals(DownloadService.NormalizeHash(app.ExpectedSha256), StringComparison.OrdinalIgnoreCase))
            {
                context.Report(app, InstallStatus.Failed, CoreText.Get("LocalFileSourceInstaller_SHA256VerificationFailed"));
                return InstallResult.Fail(CoreText.Format("LocalFileSourceInstaller_LocalInstallerHashMismatchExpected", app.ExpectedSha256, actual));
            }

            context.Line(CoreText.Get("LocalFileSourceInstaller_LocalInstallerSHA256Verified"));
        }

        var extension = Path.GetExtension(app.LocalPath);
        var kind = extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
            ? GitHubAssetKind.PortableArchive
            : GitHubAssetKind.Installer;
        return await _files.InstallAsync(app, app.LocalPath, kind, context, cancellationToken).ConfigureAwait(false);
    }
}
