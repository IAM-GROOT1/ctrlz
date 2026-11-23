// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Localization;
using System.Security.Cryptography.X509Certificates;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Models;
using CtrlZ.Core.Services;

namespace CtrlZ.Core.Safety;

/// <summary>
/// Performs every check an install would need, without installing anything.
/// <para>
/// winget ids are resolved through a search, GitHub assets through the release API, direct
/// URLs through a HEAD request and local files through an existence and signature check.
/// Nothing is downloaded to disk, no installer is run, and the profile is never modified.
/// The network calls are read-only lookups, so a dry run can be slower but can never install.
/// </para>
/// </summary>
public sealed class DryRunRunner
{
    private static readonly TimeSpan HeadTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<string, CancellationToken, Task<IReadOnlyList<WingetPackage>>> _searchWinget;
    private readonly Func<string, CancellationToken, Task<GitHubRelease>> _latestRelease;
    private readonly Func<string, CancellationToken, Task<long?>> _head;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, bool?> _isSigned;

    public DryRunRunner(
        Func<string, CancellationToken, Task<IReadOnlyList<WingetPackage>>> searchWinget,
        Func<string, CancellationToken, Task<GitHubRelease>> latestRelease,
        Func<string, CancellationToken, Task<long?>> head,
        Func<string, bool>? fileExists = null,
        Func<string, bool?>? isSigned = null)
    {
        _searchWinget = searchWinget;
        _latestRelease = latestRelease;
        _head = head;
        _fileExists = fileExists ?? SafeFileExists;
        _isSigned = isSigned ?? ProbeSignature;
    }

    public async Task<DryRunReport> RunAsync(
        InstallPlan plan,
        IReadOnlyDictionary<string, AppEntry> appsById,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<DryRunItem>();
        var index = 0;

        foreach (var item in plan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;
            progress?.Report(CoreText.Format("DryRunRunner_Checking", item.Name, index, plan.Count));

            appsById.TryGetValue(item.AppId, out var app);
            results.Add(await CheckAsync(item, app, cancellationToken).ConfigureAwait(false));
        }

        return new DryRunReport
        {
            ProfileName = plan.ProfileName,
            Items = results
        };
    }

    private async Task<DryRunItem> CheckAsync(InstallPlanItem item, AppEntry? app, CancellationToken cancellationToken)
    {
        if (app is null)
        {
            return Result(item, PlanCheckStatus.Failed, CoreText.Get("DryRunRunner_TheAppIsNoLonger"));
        }

        try
        {
            return app.Source switch
            {
                AppSource.Winget or AppSource.MicrosoftStore => await CheckWingetAsync(item, app, cancellationToken).ConfigureAwait(false),
                AppSource.GitHub => await CheckGitHubAsync(item, app, cancellationToken).ConfigureAwait(false),
                AppSource.DirectUrl => await CheckUrlAsync(item, app, cancellationToken).ConfigureAwait(false),
                AppSource.LocalFile => CheckLocalFile(item, app),
                _ => Result(item, PlanCheckStatus.Failed, CoreText.Get("DryRunRunner_UnsupportedSource"))
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result(item, PlanCheckStatus.Failed, CoreText.Get("DryRunRunner_TheCheckFailed") + ex.Message);
        }
    }

    private async Task<DryRunItem> CheckWingetAsync(InstallPlanItem item, AppEntry app, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(app.PackageId))
        {
            return Result(item, PlanCheckStatus.Failed, CoreText.Get("DryRunRunner_NoWingetPackageIdIs"));
        }

        var found = await _searchWinget(app.PackageId, cancellationToken).ConfigureAwait(false);
        var exact = found.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, app.PackageId, StringComparison.OrdinalIgnoreCase));

        if (exact is null)
        {
            return Result(item, PlanCheckStatus.Failed, CoreText.Format("DryRunRunner_WingetCannotFind", app.PackageId));
        }

        var version = string.IsNullOrWhiteSpace(exact.AvailableVersion)
            ? exact.Version
            : exact.AvailableVersion;

        var message = string.IsNullOrWhiteSpace(version)
            ? CoreText.Format("DryRunRunner_WingetResolves", exact.Id)
            : CoreText.Format("DryRunRunner_WingetResolvesVersion", exact.Id, version);

        return Result(item, PlanCheckStatus.Ready, message);
    }

    private async Task<DryRunItem> CheckGitHubAsync(InstallPlanItem item, AppEntry app, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(app.GitHubRepo))
        {
            return Result(item, PlanCheckStatus.Failed, CoreText.Get("DryRunRunner_NoGitHubRepositoryIsRecorded"));
        }

        var release = await _latestRelease(app.GitHubRepo, cancellationToken).ConfigureAwait(false);
        var asset = PickAsset(release, app);

        if (asset is null)
        {
            return Result(item, PlanCheckStatus.Failed,
                CoreText.Format("DryRunRunner_ReleaseHasNoAssetThat", release.TagName));
        }

        var size = asset.Size > 0 ? asset.Size : (long?)null;

        // A silent substitution is exactly what happens at install time, but the user should
        // know the recorded file is gone and something else will run instead.
        if (!string.IsNullOrWhiteSpace(app.GitHubAsset) &&
            !asset.Name.Equals(app.GitHubAsset, StringComparison.OrdinalIgnoreCase))
        {
            return Result(
                item,
                PlanCheckStatus.Warning,
                CoreText.Format("DryRunRunner_TheRecordedAssetIsNot", app.GitHubAsset, release.TagName) +
                CoreText.Format("DryRunRunner_TheInstallerWillFallBack", asset.Name),
                size);
        }

        return Result(item, PlanCheckStatus.Ready, CoreText.Format("DryRunRunner_ReleasePublishes", release.TagName, asset.Name), size);
    }

    private async Task<DryRunItem> CheckUrlAsync(InstallPlanItem item, AppEntry app, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(app.DownloadUrl))
        {
            return Result(item, PlanCheckStatus.Failed, CoreText.Get("DryRunRunner_NoDownloadURLIsRecorded"));
        }

        try
        {
            var bytes = await _head(app.DownloadUrl, cancellationToken).ConfigureAwait(false);
            var message = bytes is > 0
                ? CoreText.Format("DryRunRunner_TheLinkResponds", FileUtil.FormatBytes(bytes.Value))
                : CoreText.Get("DryRunRunner_TheLinkResponds2");

            return Result(item, PlanCheckStatus.Ready, message, bytes);
        }
        catch (DownloadException ex) when (ex.StatusCode == 405)
        {
            return Result(item, PlanCheckStatus.Warning,
                CoreText.Get("DryRunRunner_TheServerDoesNotSupport"));
        }
        catch (DownloadException ex)
        {
            return Result(item, PlanCheckStatus.Failed, CoreText.Get("DryRunRunner_TheLinkDidNotRespond") + ex.Message);
        }
    }

    private DryRunItem CheckLocalFile(InstallPlanItem item, AppEntry app)
    {
        if (string.IsNullOrWhiteSpace(app.LocalPath))
        {
            return Result(item, PlanCheckStatus.Failed, CoreText.Get("DryRunRunner_NoLocalFileIsRecorded"));
        }

        if (!_fileExists(app.LocalPath))
        {
            return Result(item, PlanCheckStatus.Failed, CoreText.Get("DryRunRunner_TheInstallerFileIsNot"));
        }

        long? size = null;
        try
        {
            size = new FileInfo(app.LocalPath).Length;
        }
        catch
        {
            // Size is informational only.
        }

        return _isSigned(app.LocalPath) == false
            ? Result(item, PlanCheckStatus.Warning, CoreText.Get("DryRunRunner_TheInstallerIsPresentBut"), size)
            : Result(item, PlanCheckStatus.Ready, CoreText.Get("DryRunRunner_TheInstallerFileIsPresent"), size);
    }

    private static GitHubAsset? PickAsset(GitHubRelease release, AppEntry app)
    {
        if (!string.IsNullOrWhiteSpace(app.GitHubAsset))
        {
            var exact = release.Assets.FirstOrDefault(asset =>
                asset.Name.Equals(app.GitHubAsset, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }
        }

        if (!string.IsNullOrWhiteSpace(app.GitHubAssetExtension))
        {
            var byExtension = release.Assets.FirstOrDefault(asset =>
                asset.Extension.Equals(app.GitHubAssetExtension, StringComparison.OrdinalIgnoreCase));
            if (byExtension is not null)
            {
                return byExtension;
            }
        }

        return release.Assets.FirstOrDefault();
    }

    private static DryRunItem Result(
        InstallPlanItem item,
        PlanCheckStatus status,
        string message,
        long? bytes = null) => new()
    {
        AppId = item.AppId,
        Name = item.Name,
        Source = item.Source,
        Status = status,
        Message = message,
        DownloadBytes = bytes
    };

    private static bool SafeFileExists(string path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static bool? ProbeSignature(string path)
    {
        try
        {
            using var certificate = X509Certificate.CreateFromSignedFile(path);
            return certificate is not null;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
        catch
        {
            return null;
        }
    }
}
