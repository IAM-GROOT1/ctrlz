// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Localization;
using System.Text.RegularExpressions;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Models;
using CtrlZ.Core.Services;

namespace CtrlZ.Core.Install;

public sealed partial class WingetSourceInstaller : ISourceInstaller
{
    private readonly WingetCli _winget;

    public WingetSourceInstaller(WingetCli winget)
    {
        _winget = winget;
    }

    public bool CanHandle(AppSource source) => source is AppSource.Winget or AppSource.MicrosoftStore;

    [GeneratedRegex(@"(\d{1,3})\s*%")]
    private static partial Regex PercentRegex();

    public async Task<InstallResult> InstallAsync(AppEntry app, InstallContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(app.PackageId))
        {
            return InstallResult.NeedsAction(
                CoreText.Get("WingetSourceInstaller_NoWingetPackageIdIs"));
        }

        if (!await _winget.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return InstallResult.NeedsAction(
                CoreText.Get("WingetSourceInstaller_WingetIsNotAvailableInstall"));
        }

        // Product ids from the Microsoft Store source are 12-character alphanumeric ids.
        // A winget id such as Microsoft.PowerToys must not be forced to msstore.
        var source = app.Source == AppSource.MicrosoftStore &&
                     Regex.IsMatch(app.PackageId, "^9[A-Za-z0-9]{11}$")
            ? "msstore"
            : null;

        context.Line($"winget install --id {app.PackageId} --exact --silent" + (source is null ? string.Empty : $" --source {source}"));
        var lastPercent = -1;
        var result = await _winget.InstallAsync(
            app.PackageId,
            source,
            line =>
            {
                var text = CleanLine(line.Text);
                if (string.IsNullOrWhiteSpace(text))
                {
                    return;
                }

                context.Line(text);
                var match = PercentRegex().Match(text);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var percent) &&
                    percent is >= 0 and <= 100 && percent > lastPercent)
                {
                    lastPercent = percent;
                    context.Report(app, InstallStatus.Installing, CoreText.Format("WingetSourceInstaller_DownloadingOrInstalling", percent), percent / 100d);
                }
            },
            cancellationToken).ConfigureAwait(false);

        var combined = result.CombinedOutput;
        if (result.ExitCode == 0 ||
            combined.Contains("already installed", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("No available upgrade", StringComparison.OrdinalIgnoreCase))
        {
            context.Report(app, InstallStatus.Success, CoreText.Get("WingetSourceInstaller_InstalledWithWinget"), 1);
            return InstallResult.Ok(CoreText.Get("WingetSourceInstaller_InstalledWithWinget"));
        }

        if (result.ExitCode is 3010 or 1641 ||
            combined.Contains("reboot", StringComparison.OrdinalIgnoreCase) && result.ExitCode == 0)
        {
            context.Report(app, InstallStatus.RebootRequired, CoreText.Get("WingetSourceInstaller_ARestartIsNeededTo"), 1);
            return InstallResult.Reboot(CoreText.Get("WingetSourceInstaller_InstalledButWindowsNeedsA"));
        }

        if (result.ExitCode == -1978335189 || result.ExitCode == unchecked((int)0x8A15002B))
        {
            context.Report(app, InstallStatus.Success, CoreText.Get("WingetSourceInstaller_AlreadyInstalled"), 1);
            return InstallResult.Ok(CoreText.Get("WingetSourceInstaller_AlreadyInstalled"));
        }

        if (result.ExitCode == 1618 || combined.Contains("another installation is in progress", StringComparison.OrdinalIgnoreCase))
        {
            return InstallResult.NeedsAction(CoreText.Get("WingetSourceInstaller_AnotherInstallerIsAlreadyRunning"));
        }

        if (result.TimedOut)
        {
            return InstallResult.Fail(CoreText.Get("WingetSourceInstaller_WingetTimedOutAfter45"));
        }

        var error = ExtractError(combined, result.ExitCode);
        context.Report(app, InstallStatus.Failed, error);
        return InstallResult.Fail(error, result.ExitCode);
    }

    private static string CleanLine(string text)
    {
        var cleaned = new string(text.Where(c => c is not ('\r' or '\b' or '\0')).ToArray()).Trim();
        return cleaned;
    }

    private static string ExtractError(string output, int exitCode)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.Contains('%') && !line.StartsWith("â", StringComparison.Ordinal))
            .TakeLast(4)
            .ToArray();
        var text = string.Join(' ', lines);
        if (text.Length > 500)
        {
            text = text[..500];
        }

        return string.IsNullOrWhiteSpace(text)
            ? CoreText.Format("WingetSourceInstaller_WingetFailedWithExitCode", exitCode)
            : CoreText.Format("WingetSourceInstaller_WingetFailed", exitCode, text);
    }
}
