// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

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
            return InstallResult.NeedsAction("This app has no GitHub repository configured.");
        }

        try
        {
            context.Report(app, InstallStatus.Preparing, "Checking the latest GitHub release…");
            context.Line($"Resolving latest release for {app.GitHubRepo}…");
            var release = await _github.GetLatestAsync(app.GitHubRepo, cancellationToken).ConfigureAwait(false);
            var asset = _github.ChooseInstallAsset(release, app);
            if (asset is null)
            {
                return InstallResult.NeedsAction(
                    $"No downloadable Windows asset was found in {release.TagName}. Open {release.HtmlUrl} and add a direct link instead.");
            }

            context.Line($"Release {release.TagName}: downloading {asset.Name} ({asset.SizeLabel}).");
            var expectedHash = app.ExpectedSha256;
            if (!string.IsNullOrWhiteSpace(asset.Sha256))
            {
                if (!string.IsNullOrWhiteSpace(expectedHash) &&
                    !DownloadService.NormalizeHash(expectedHash).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return InstallResult.Fail(
                        "The saved SHA-256 does not match the latest GitHub release. Review the app entry.");
                }

                expectedHash = asset.Sha256;
            }

            var download = await _downloads.DownloadAsync(
                asset.DownloadUrl,
                asset.Name,
                expectedHash,
                new Progress<DownloadProgress>(p =>
                    context.Report(app, InstallStatus.Downloading, $"Downloading {asset.Name}…", p.Fraction)),
                cancellationToken).ConfigureAwait(false);

            var kind = app.GitHubAssetKind;
            if (kind == GitHubAssetKind.Installer && asset.Extension == ".zip")
            {
                context.Line("Asset is a ZIP; CTRL+Z will look for a bundled installer.");
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
            context.Report(app, InstallStatus.Failed, "SHA-256 verification failed.");
            return InstallResult.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            context.Report(app, InstallStatus.Failed, ex.Message);
            return InstallResult.Fail($"GitHub install failed: {ex.Message}");
        }
    }
}
