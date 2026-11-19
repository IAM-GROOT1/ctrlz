// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

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
            progress?.Report($"Checking {item.Name} ({index}/{plan.Count})…");

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
            return Result(item, PlanCheckStatus.Failed, "The app is no longer in the profile.");
        }

        try
        {
            return app.Source switch
            {
                AppSource.Winget or AppSource.MicrosoftStore => await CheckWingetAsync(item, app, cancellationToken).ConfigureAwait(false),
                AppSource.GitHub => await CheckGitHubAsync(item, app, cancellationToken).ConfigureAwait(false),
                AppSource.DirectUrl => await CheckUrlAsync(item, app, cancellationToken).ConfigureAwait(false),
                AppSource.LocalFile => CheckLocalFile(item, app),
                _ => Result(item, PlanCheckStatus.Failed, "Unsupported source.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result(item, PlanCheckStatus.Failed, "The check failed: " + ex.Message);
        }
    }

    private async Task<DryRunItem> CheckWingetAsync(InstallPlanItem item, AppEntry app, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(app.PackageId))
        {
            return Result(item, PlanCheckStatus.Failed, "No winget package id is recorded.");
        }

        var found = await _searchWinget(app.PackageId, cancellationToken).ConfigureAwait(false);
        var exact = found.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, app.PackageId, StringComparison.OrdinalIgnoreCase));

        if (exact is null)
        {
            return Result(item, PlanCheckStatus.Failed, $"winget cannot find '{app.PackageId}'.");
        }

        var version = string.IsNullOrWhiteSpace(exact.AvailableVersion)
            ? exact.Version
            : exact.AvailableVersion;

        var message = string.IsNullOrWhiteSpace(version)
            ? $"winget resolves {exact.Id}."
            : $"winget resolves {exact.Id} (version {version}).";

        return Result(item, PlanCheckStatus.Ready, message);
    }

    private async Task<DryRunItem> CheckGitHubAsync(InstallPlanItem item, AppEntry app, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(app.GitHubRepo))
        {
            return Result(item, PlanCheckStatus.Failed, "No GitHub repository is recorded.");
        }

        var release = await _latestRelease(app.GitHubRepo, cancellationToken).ConfigureAwait(false);
        var asset = PickAsset(release, app);

        if (asset is null)
        {
            return Result(item, PlanCheckStatus.Failed,
                $"Release {release.TagName} has no asset that matches this app.");
        }

        return Result(
            item,
            PlanCheckStatus.Ready,
            $"Release {release.TagName} publishes {asset.Name}.",
            asset.Size > 0 ? asset.Size : null);
    }

    private async Task<DryRunItem> CheckUrlAsync(InstallPlanItem item, AppEntry app, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(app.DownloadUrl))
        {
            return Result(item, PlanCheckStatus.Failed, "No download URL is recorded.");
        }

        try
        {
            var bytes = await _head(app.DownloadUrl, cancellationToken).ConfigureAwait(false);
            var message = bytes is > 0
                ? $"The link responds ({FileUtil.FormatBytes(bytes.Value)})."
                : "The link responds.";

            return Result(item, PlanCheckStatus.Ready, message, bytes);
        }
        catch (DownloadException ex) when (ex.StatusCode == 405)
        {
            return Result(item, PlanCheckStatus.Warning,
                "The server does not support a HEAD check, so the link could not be verified.");
        }
        catch (DownloadException ex)
        {
            return Result(item, PlanCheckStatus.Failed, "The link did not respond: " + ex.Message);
        }
    }

    private DryRunItem CheckLocalFile(InstallPlanItem item, AppEntry app)
    {
        if (string.IsNullOrWhiteSpace(app.LocalPath))
        {
            return Result(item, PlanCheckStatus.Failed, "No local file is recorded.");
        }

        if (!_fileExists(app.LocalPath))
        {
            return Result(item, PlanCheckStatus.Failed, "The installer file is not on this PC.");
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
            ? Result(item, PlanCheckStatus.Warning, "The installer is present but is not code-signed.", size)
            : Result(item, PlanCheckStatus.Ready, "The installer file is present.", size);
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
