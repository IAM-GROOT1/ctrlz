// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.App.Localization;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CtrlZ.App.Views;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Services;
using CtrlZ.Core.Updates;

namespace CtrlZ.App.Services;

/// <summary>
/// Orchestrates the whole update flow: startup check, prompt, download with progress,
/// verification and handing off to the update helper.
/// </summary>
public sealed class UpdateCoordinator
{
    private static readonly TimeSpan RemindDelay = TimeSpan.FromHours(24);
    private static readonly TimeSpan DeferralTimeout = TimeSpan.FromMinutes(30);

    private readonly AppServices _services;
    private readonly DialogService _dialogs;
    private readonly Func<Window?> _ownerAccessor;
    private readonly UpdateService _updateService;
    private readonly UpdateDownloader _downloader;
    private readonly IInstallLocationProbe _installProbe;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private bool _promptOpen;

    public UpdateCoordinator(AppServices services, DialogService dialogs, Func<Window?> ownerAccessor)
    {
        _services = services;
        _dialogs = dialogs;
        _ownerAccessor = ownerAccessor;
        _updateService = new UpdateService(new GitHubReleaseFeed(services.Downloads), services.Log);
        _downloader = new UpdateDownloader(services.Downloads, services.Log);
        _installProbe = new RegistryInstallLocationProbe();
    }

    public string CurrentVersion => AppVersionInfo.Display;

    /// <summary>True while a prompt or a download is on screen.</summary>
    public bool IsPrompting => _promptOpen;

    /// <summary>
    /// Startup entry point. Reports a failed apply from a previous run, then checks quietly.
    /// Never shows an error for network problems.
    /// </summary>
    public async Task RunStartupCheckAsync()
    {
        try
        {
            ReportPendingResult();

            var settings = _services.State.Settings;
            if (!settings.AutoCheckForUpdates)
            {
                return;
            }

            var check = await CheckAsync(CancellationToken.None).ConfigureAwait(true);
            if (!check.UpdateAvailable)
            {
                return;
            }

            var decision = UpdatePromptPolicy.Decide(
                UpdatePromptState.FromSettings(settings),
                check.Latest!.Version,
                DateTimeOffset.UtcNow,
                manual: false);

            if (decision != UpdatePromptDecision.Show)
            {
                _services.Log.Write(LogLevel.Info, $"Startup update prompt suppressed: {decision}.");
                return;
            }

            // Never prompt over a running install queue, backup or restore.
            if (!await ActivityWaiter.WaitForIdleAsync(_services.Activity, DeferralTimeout).ConfigureAwait(true))
            {
                _services.Log.Write(LogLevel.Info, "Update prompt deferred: another operation is still running.");
                return;
            }

            await PromptAsync(check, manual: false).ConfigureAwait(true);
        }
        catch (UpdateCheckException ex)
        {
            // Fail quietly: offline, rate-limited or a malformed response.
            _services.Log.Write(LogLevel.Info, $"Background update check failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            _services.Log.Write(LogLevel.Warning, "Background update check failed unexpectedly.", ex);
        }
    }

    /// <summary>Settings button: reports the outcome to the user, including failures.</summary>
    public async Task CheckManuallyAsync()
    {
        try
        {
            var check = await CheckAsync(CancellationToken.None).ConfigureAwait(true);

            if (check.UpdateAvailable)
            {
                if (!await ActivityWaiter.WaitForIdleAsync(_services.Activity, DeferralTimeout).ConfigureAwait(true))
                {
                    _dialogs.Info(
                        Loc.Get("Update_AnInstallBackupOrRestore"),
                        Loc.Get("Update_UpdateDeferred"));
                    return;
                }

                await PromptAsync(check, manual: true).ConfigureAwait(true);
                return;
            }

            _dialogs.Info(
                check.Latest is null
                    ? Loc.Format("Update_YouAreRunningCTRLZ", CurrentVersion)
                    : Loc.Format("Update_YouAreRunningCTRLZ2", CurrentVersion),
                Loc.Get("Update_NoUpdates"));
        }
        catch (UpdateCheckException ex)
        {
            _services.Log.Write(LogLevel.Info, $"Manual update check failed: {ex.Message}");
            _dialogs.Info(ex.FriendlyMessage, Loc.Get("Update_CouldNotCheckForUpdates"));
        }
    }

    private async Task<UpdateCheck> CheckAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            var settings = _services.State.Settings;
            var check = await _updateService
                .CheckAsync(AppVersionInfo.Current, settings.UpdateChannel, cancellationToken)
                .ConfigureAwait(true);

            settings.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
            _services.State.SaveSettings();
            return check;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PromptAsync(UpdateCheck check, bool manual)
    {
        var release = check.Latest;
        if (release is null || _promptOpen)
        {
            return;
        }

        var settings = _services.State.Settings;
        var decision = UpdatePromptPolicy.Decide(
            UpdatePromptState.FromSettings(settings),
            release.Version,
            DateTimeOffset.UtcNow,
            manual);

        if (decision != UpdatePromptDecision.Show)
        {
            if (manual && decision == UpdatePromptDecision.Skipped)
            {
                _dialogs.Info(
                    Loc.Format("Update_CTRLZIsAvailableBut", release.Version) +
                    Loc.Get("Update_ANewerReleaseWillPrompt"),
                    Loc.Get("Update_ThisVersionIsSkipped"));
            }

            return;
        }

        var kind = InstallKindDetector.Detect(Environment.ProcessPath, _installProbe);

        if (!UpdateAssetSelector.TrySelect(release, kind, out var selection, out var assetError))
        {
            _services.Log.Write(LogLevel.Warning, $"Update {release.Version} cannot be offered: {assetError}");
            if (manual)
            {
                _dialogs.Info(assetError, Loc.Get("Update_UpdateNotAvailable"));
            }

            return;
        }

        _promptOpen = true;
        var window = new UpdatePromptWindow();
        try
        {
            window.Configure(CurrentVersion, release, ReleaseNotesFormatter.ToPlainText(release.Notes));

            var outcome = await window.PromptAsync(_ownerAccessor()).ConfigureAwait(true);

            switch (outcome)
            {
                case UpdatePromptOutcome.RemindLater:
                    settings.UpdateRemindAfterUtc = DateTimeOffset.UtcNow + RemindDelay;
                    _services.State.SaveSettings();
                    window.CloseFromCoordinator();
                    return;

                case UpdatePromptOutcome.SkipVersion:
                    settings.SkippedUpdateVersion = release.Version.ToString();
                    settings.UpdateRemindAfterUtc = null;
                    _services.State.SaveSettings();
                    window.CloseFromCoordinator();
                    return;

                case UpdatePromptOutcome.Dismissed:
                    window.CloseFromCoordinator();
                    return;
            }

            await DownloadAndApplyAsync(window, check, selection!, kind).ConfigureAwait(true);
        }
        finally
        {
            _promptOpen = false;
        }
    }

    private async Task DownloadAndApplyAsync(
        UpdatePromptWindow window,
        UpdateCheck check,
        UpdateAssetSelection selection,
        UpdateInstallKind kind)
    {
        var release = check.Latest!;
        using var cts = new CancellationTokenSource();
        window.CancelRequested += (_, _) => cts.Cancel();

        window.BeginProgress(Loc.Get("Update_Preparing"));

        try
        {
            var expectedHash = await _downloader
                .ResolveExpectedHashAsync(release, selection.Asset, cts.Token)
                .ConfigureAwait(true);

            var progress = new Progress<DownloadProgress>(report =>
            {
                if (report.Fraction is { } fraction)
                {
                    window.SetProgress(fraction, Loc.Format("Update_Downloading", fraction));
                }
                else
                {
                    window.SetProgress(null, Loc.Get("Update_Downloading2"));
                }
            });

            window.SetProgress(0, "Downloading\u2026 0%");

            var file = await _downloader
                .DownloadAsync(release, selection.Asset, expectedHash, progress, cts.Token)
                .ConfigureAwait(true);

            window.SetProgress(null, Loc.Get("Update_Verifying"));
            var actual = await UpdateVerifier.ComputeSha256Async(file.Path, cts.Token).ConfigureAwait(true);
            UpdateVerifier.EnsureHash(expectedHash, actual);

            var companion = kind == UpdateInstallKind.Portable
                ? await TryDownloadCompanionAsync(release, cts.Token).ConfigureAwait(true)
                : null;

            // A long operation may have started while the download ran; never interrupt it.
            if (_services.Activity.IsBusy)
            {
                window.SetProgress(null, Loc.Get("Update_WaitingForTheCurrentOperation"));
                _services.Log.Write(LogLevel.Info, "Update download finished; waiting for the running operation to finish.");
                if (!await ActivityWaiter.WaitForIdleAsync(_services.Activity, DeferralTimeout, cts.Token).ConfigureAwait(true))
                {
                    _services.Log.Write(LogLevel.Info, "Update postponed: an operation is still running after the deferral window.");
                    window.ShowError(Loc.Get("Update_AnInstallOrBackupIs"));
                    return;
                }
            }

            // From here to the helper launch everything runs on the UI thread, so no in-app
            // operation can start in between. The install lock also covers a command-line install
            // in another process; it stays held until this process exits, unless the helper launch fails.
            if (_services.Activity.IsBusy || !TryHoldInstallLockUntilExit())
            {
                _services.Log.Write(LogLevel.Info, "Update postponed: an install or backup is running.");
                window.ShowError(Loc.Get("Update_AnInstallOrBackupIs2"));
                return;
            }

            window.SetProgress(null, Loc.Get("Update_RestartingToFinishTheUpdate"));
            try
            {
                LaunchHelper(file.Path, expectedHash, kind, release.Version.ToString(), companion);
            }
            catch
            {
                // The app keeps running: never keep installs blocked.
                ReleaseUpdateLock();
                throw;
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            _services.Log.Write(LogLevel.Info, "The update download was cancelled by the user.");
            window.ShowError(UpdateFailureMessages.ForCancellationDisplay(userRequested: true));
        }
        catch (OperationCanceledException)
        {
            // A request timeout or a dropped connection lands here, not a user cancellation.
            _services.Log.Write(LogLevel.Warning, "The update download timed out.");
            window.ShowError(UpdateFailureMessages.ForCancellationDisplay(userRequested: false));
        }
        catch (Exception ex) when (ex is UpdateVerificationException or UpdateCheckException or IOException or UnauthorizedAccessException)
        {
            _services.Log.Write(LogLevel.Error, "The update could not be applied.", ex);
            // The English detail stays in the log; the user sees why in their own language.
            window.ShowError(ex is UpdateCheckException failedCheck ? failedCheck.FriendlyMessage : CtrlZ.Core.Localization.CoreText.Get("UpdateDownload_Failed"));
        }
    }

    private CtrlZ.Core.Install.InstallLock? _updateLock;

    /// <summary>Takes the install lock for the rest of this process's life, so no install starts during the update.</summary>
    private bool TryHoldInstallLockUntilExit()
    {
        _updateLock ??= CtrlZ.Core.Install.InstallLock.TryAcquire();
        return _updateLock is not null;
    }

    private void ReleaseUpdateLock()
    {
        _updateLock?.Dispose();
        _updateLock = null;
    }

    /// <summary>
    /// A portable copy with <c>ctrlz.exe</c> beside it also gets this release's command line,
    /// verified against SHA256SUMS.txt. Any problem only skips the command line, never the app.
    /// </summary>
    private async Task<(string Path, string Sha256, string Target)?> TryDownloadCompanionAsync(
        ReleaseInfo release, CancellationToken cancellationToken)
    {
        var appExe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "CTRL+Z.exe");
        var cliTarget = Path.Combine(Path.GetDirectoryName(appExe) ?? AppContext.BaseDirectory, "ctrlz.exe");
        if (!File.Exists(cliTarget))
        {
            return null;
        }

        if (!UpdateAssetSelector.TrySelectCli(release, out var selection, out var error))
        {
            _services.Log.Write(LogLevel.Warning, "ctrlz.exe is not updated with this release: " + error);
            return null;
        }

        try
        {
            var hash = await _downloader.ResolveExpectedHashAsync(release, selection!.Asset, cancellationToken)
                .ConfigureAwait(true);
            var file = await _downloader.DownloadAsync(release, selection.Asset, hash, null, cancellationToken)
                .ConfigureAwait(true);
            var actual = await UpdateVerifier.ComputeSha256Async(file.Path, cancellationToken).ConfigureAwait(true);
            UpdateVerifier.EnsureHash(hash, actual);
            return (file.Path, hash, cliTarget);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _services.Log.Write(LogLevel.Warning, "ctrlz.exe is not updated with this release; the app still updates.", ex);
            return null;
        }
    }

    /// <summary>
    /// Copies the running exe to a temp folder and relaunches it in <c>--apply-update</c>
    /// mode, because a portable build is a single exe and cannot ship a separate helper.
    /// </summary>
    private void LaunchHelper(
        string downloadedFile,
        string expectedSha256,
        UpdateInstallKind kind,
        string version,
        (string Path, string Sha256, string Target)? companion = null)
    {
        var target = Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "CTRL+Z.exe");

        var tempDir = Path.Combine(Path.GetTempPath(), "CTRL+Z-Update", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDir);

        var requestPath = Path.Combine(tempDir, "apply.json");
        UpdateApplyFiles.WriteRequest(requestPath, new UpdateApplyRequest
        {
            Kind = kind == UpdateInstallKind.Installed
                ? nameof(UpdateInstallKind.Installed)
                : nameof(UpdateInstallKind.Portable),
            DownloadedFile = downloadedFile,
            TargetExe = target,
            ExpectedSha256 = expectedSha256,
            Version = version,
            WaitForProcessId = Environment.ProcessId,
            CompanionDownloadedFile = companion?.Path,
            CompanionExpectedSha256 = companion?.Sha256,
            CompanionTargetExe = companion?.Target
        });

        var helper = Path.Combine(tempDir, "CTRL+Z-updater.exe");
        File.Copy(target, helper, overwrite: true);

        Process.Start(new ProcessStartInfo(helper)
        {
            UseShellExecute = false,
            WorkingDirectory = tempDir,
            Arguments = $"--apply-update \"{requestPath}\""
        });

        _services.Log.Write(LogLevel.Info, $"Update helper started; CTRL+Z is exiting to apply {version}.");

        if (Application.Current?.MainWindow is { } main)
        {
            main.Hide();
        }

        Application.Current?.Shutdown();
    }

    /// <summary>
    /// Surfaces the outcome of an apply that happened while the app was closed, then clears
    /// the marker so the message appears once.
    /// </summary>
    private void ReportPendingResult()
    {
        var path = Path.Combine(_services.Paths.State, UpdateApplyFiles.ResultFileName);
        var result = UpdateApplyFiles.ReadResult(path);
        UpdateApplyFiles.DeleteIfExists(path);

        if (result is null || result.Success)
        {
            return;
        }

        _services.Log.Write(LogLevel.Warning, $"The previous update did not complete: {result.Message}");
        var message = result.Message == App.UpdateRolledBackMessage
            ? Loc.Get("App_UpdateRolledBack")
            : result.DisplayMessage ?? Loc.Get("Update_ThePreviousUpdateDidNot");

        // A build that failed to start would only fail again: treat it like "Skip this version",
        // so it is not offered until a newer release appears.
        // Only a version newer than this one can have been rolled back from, so an edited result
        // file cannot use this to hide every future update.
        if (result.RolledBack && SemanticVersion.TryParse(result.Version, out var failed) && failed is not null &&
            failed.CompareTo(AppVersionInfo.Current) > 0)
        {
            _services.State.Settings.SkippedUpdateVersion = failed.ToString();
            _services.State.Settings.UpdateRemindAfterUtc = null;
            _services.State.SaveSettings();
            _services.Log.Write(LogLevel.Info, $"Version {failed} rolled back; it is skipped until a newer release appears.");
            message += Loc.Format("Update_RolledBackVersionSkipped", failed);
        }

        _dialogs.Error(message, Loc.Get("Update_UpdateNotApplied"));
    }
}
