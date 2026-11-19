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
                        "An install, backup or restore is still running, so the update prompt is deferred until it finishes.",
                        "Update deferred");
                    return;
                }

                await PromptAsync(check, manual: true).ConfigureAwait(true);
                return;
            }

            _dialogs.Info(
                check.Latest is null
                    ? $"You are running CTRL+Z {CurrentVersion}. No published release was found for this channel yet."
                    : $"You are running CTRL+Z {CurrentVersion}, which is up to date.",
                "No updates");
        }
        catch (UpdateCheckException ex)
        {
            _services.Log.Write(LogLevel.Info, $"Manual update check failed: {ex.Message}");
            _dialogs.Info(ex.FriendlyMessage, "Could not check for updates");
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
                    $"CTRL+Z {release.Version} is available, but you asked to skip that version. " +
                    "A newer release will prompt again.",
                    "This version is skipped");
            }

            return;
        }

        var kind = InstallKindDetector.Detect(Environment.ProcessPath, _installProbe);

        if (!UpdateAssetSelector.TrySelect(release, kind, out var selection, out var assetError))
        {
            _services.Log.Write(LogLevel.Warning, $"Update {release.Version} cannot be offered: {assetError}");
            if (manual)
            {
                _dialogs.Info(assetError, "Update not available");
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

        window.BeginProgress("Preparing\u2026");

        try
        {
            var expectedHash = await _downloader
                .ResolveExpectedHashAsync(release, selection.Asset, cts.Token)
                .ConfigureAwait(true);

            var progress = new Progress<DownloadProgress>(report =>
            {
                if (report.Fraction is { } fraction)
                {
                    window.SetProgress(fraction, $"Downloading\u2026 {fraction:P0}");
                }
                else
                {
                    window.SetProgress(null, "Downloading\u2026");
                }
            });

            window.SetProgress(0, "Downloading\u2026 0%");

            var file = await _downloader
                .DownloadAsync(release, selection.Asset, expectedHash, progress, cts.Token)
                .ConfigureAwait(true);

            window.SetProgress(null, "Verifying\u2026");
            var actual = await UpdateVerifier.ComputeSha256Async(file.Path, cts.Token).ConfigureAwait(true);
            UpdateVerifier.EnsureHash(expectedHash, actual);

            // A long operation may have started while the download ran; never interrupt it.
            if (_services.Activity.IsBusy)
            {
                window.SetProgress(null, "Waiting for the current operation to finish\u2026");
                _services.Log.Write(LogLevel.Info, "Update download finished; waiting for the running operation to finish.");
                await ActivityWaiter.WaitForIdleAsync(_services.Activity, DeferralTimeout, cts.Token).ConfigureAwait(true);
            }

            window.SetProgress(null, "Restarting to finish the update\u2026");
            LaunchHelper(file.Path, expectedHash, kind, release.Version.ToString());
        }
        catch (OperationCanceledException)
        {
            _services.Log.Write(LogLevel.Info, "The update download was cancelled by the user.");
            window.ShowError("The update was cancelled. Nothing was changed.");
        }
        catch (Exception ex) when (ex is UpdateVerificationException or UpdateCheckException or IOException or UnauthorizedAccessException)
        {
            _services.Log.Write(LogLevel.Error, "The update could not be applied.", ex);
            window.ShowError(ex.Message);
        }
    }

    /// <summary>
    /// Copies the running exe to a temp folder and relaunches it in <c>--apply-update</c>
    /// mode, because a portable build is a single exe and cannot ship a separate helper.
    /// </summary>
    private void LaunchHelper(string downloadedFile, string expectedSha256, UpdateInstallKind kind, string version)
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
            WaitForProcessId = Environment.ProcessId
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
        _dialogs.Error(
            result.Message ?? "The previous update did not complete. Your existing version is unchanged.",
            "Update not applied");
    }
}
