// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Localization;
using CtrlZ.Core.Models;
using CtrlZ.Core.Services;

namespace CtrlZ.Core.Install;

public sealed class GitHubSourceInstaller : ISourceInstaller
{
    private readonly GitHubReleaseService _github;
    private readonly DownloadService _downloads;
    private readonly FileInstallService _files;
    private readonly CtrlZ.Core.AppUpdates.GitHubReleaseTracker? _tracker;

    /// <param name="tracker">Records the release each app was installed from, for update offers.</param>
    public GitHubSourceInstaller(
        GitHubReleaseService github,
        DownloadService downloads,
        FileInstallService files,
        CtrlZ.Core.AppUpdates.GitHubReleaseTracker? tracker = null)
    {
        _github = github;
        _downloads = downloads;
        _files = files;
        _tracker = tracker;
    }

    public bool CanHandle(AppSource source) => source == AppSource.GitHub;

    public async Task<InstallResult> InstallAsync(AppEntry app, InstallContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(app.GitHubRepo))
        {
            return InstallResult.NeedsAction(CoreText.Get("GitHubSourceInstaller_ThisAppHasNoGitHub"));
        }

        try
        {
            context.Report(app, InstallStatus.Preparing, CoreText.Get("GitHubSourceInstaller_CheckingTheLatestGitHubRelease"));
            context.Line(CoreText.Format("GitHubSourceInstaller_ResolvingLatestReleaseFor", app.GitHubRepo));
            var release = await _github.GetLatestAsync(app.GitHubRepo, cancellationToken).ConfigureAwait(false);
            var asset = _github.ChooseInstallAsset(release, app);
            if (asset is null)
            {
                return InstallResult.NeedsAction(
                    CoreText.Format("GitHubSourceInstaller_NoDownloadableWindowsAssetWas", release.TagName, release.HtmlUrl));
            }

            context.Line(CoreText.Format("GitHubSourceInstaller_ReleaseDownloading", release.TagName, asset.Name, asset.SizeLabel));
            var expectedHash = app.ExpectedSha256;
            var publishedHash = asset.Sha256;
            if (string.IsNullOrWhiteSpace(publishedHash))
            {
                // GitHub reports no digest for this asset: use the release's own checksum file if it has one.
                try
                {
                    if (await _github.GetPublishedChecksumAsync(release, asset, cancellationToken).ConfigureAwait(false) is { } listed)
                    {
                        publishedHash = listed.Sha256;
                        context.Line(CoreText.Format("GitHubSourceInstaller_SHA256ForTakenFrom", asset.Name, listed.FileName));
                    }
                }
                catch (FormatException ex)
                {
                    context.Report(app, InstallStatus.Failed, CoreText.Get("GitHubSourceInstaller_TheReleaseSChecksumFile"));
                    return InstallResult.Fail(ex.Message);
                }
            }

            if (!string.IsNullOrWhiteSpace(publishedHash))
            {
                if (!string.IsNullOrWhiteSpace(expectedHash) &&
                    !DownloadService.NormalizeHash(expectedHash).Equals(publishedHash, StringComparison.OrdinalIgnoreCase))
                {
                    return InstallResult.Fail(
                        CoreText.Get("GitHubSourceInstaller_TheSavedSHA256Does"));
                }

                expectedHash = publishedHash;
            }
            else if (string.IsNullOrWhiteSpace(expectedHash))
            {
                context.Line(CoreText.Get("GitHubSourceInstaller_ThisReleasePublishesNoSHA"));
            }

            var download = await _downloads.DownloadAsync(
                asset.DownloadUrl,
                asset.Name,
                expectedHash,
                new Progress<DownloadProgress>(p =>
                    context.Report(app, InstallStatus.Downloading, CoreText.Format("GitHubSourceInstaller_Downloading", asset.Name), p.Fraction)),
                cancellationToken).ConfigureAwait(false);

            var kind = app.GitHubAssetKind;
            if (kind == GitHubAssetKind.Installer && asset.Extension == ".zip")
            {
                context.Line(CoreText.Get("GitHubSourceInstaller_AssetIsAZIPCTRL"));
            }

            var result = await _files.InstallAsync(app, download.Path, kind, context, cancellationToken).ConfigureAwait(false);
            if (result.Success || result.Status == InstallStatus.RebootRequired)
            {
                _tracker?.Record(app.Id, app.GitHubRepo!, release.TagName);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DownloadHashMismatchException ex)
        {
            context.Report(app, InstallStatus.Failed, CoreText.Get("GitHubSourceInstaller_SHA256VerificationFailed"));
            return InstallResult.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            context.Report(app, InstallStatus.Failed, ex.Message);
            return InstallResult.Fail(CoreText.Format("GitHubSourceInstaller_GitHubInstallFailed", ex.Message));
        }
    }
}
