using System.IO.Compression;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Models;
using CtrlZ.Core.Services;

namespace CtrlZ.Core.Install;

/// <summary>
/// Runs a downloaded or local installer. Silent switches are chosen per installer type
/// unless the profile explicitly supplies its own. ZIPs can be portable payloads or
/// installer bundles; the UI records which one the user chose.
/// </summary>
public sealed class FileInstallService
{
    private readonly AppPaths _paths;
    private readonly ProcessRunner _runner;
    private readonly IAppLog _log;

    public FileInstallService(AppPaths paths, ProcessRunner runner, IAppLog log)
    {
        _paths = paths;
        _runner = runner;
        _log = log;
    }

    public async Task<InstallResult> InstallAsync(
        AppEntry app,
        string localPath,
        GitHubAssetKind kind,
        InstallContext context,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(localPath))
        {
            return InstallResult.Fail($"The downloaded installer no longer exists: {localPath}");
        }

        var extension = Path.GetExtension(localPath).ToLowerInvariant();
        if (kind == GitHubAssetKind.PortableExecutable)
        {
            return CopyPortable(app, localPath, context);
        }

        if (kind == GitHubAssetKind.PortableArchive)
        {
            return extension == ".zip"
                ? ExtractPortable(app, localPath, context)
                : CopyPortable(app, localPath, context);
        }

        if (extension == ".zip")
        {
            return await ExtractAndRunInstallerAsync(app, localPath, context, cancellationToken).ConfigureAwait(false);
        }

        return await RunInstallerAsync(app, localPath, context, cancellationToken).ConfigureAwait(false);
    }

    public async Task<InstallResult> RunInstallerAsync(
        AppEntry app,
        string installerPath,
        InstallContext context,
        CancellationToken cancellationToken)
    {
        var type = SilentInstallerCatalog.Detect(installerPath);
        var extension = Path.GetExtension(installerPath).ToLowerInvariant();
        if (extension is ".msix" or ".appx" or ".msixbundle" or ".appxbundle")
        {
            return await InstallMsixAsync(app, installerPath, context, cancellationToken).ConfigureAwait(false);
        }

        if (extension is not (".exe" or ".msi"))
        {
            return InstallResult.NeedsAction(
                $"CTRL+Z cannot run {extension} files automatically. Use the installer .exe/.msi, or add the app as a portable archive.");
        }

        var custom = app.SilentArgs?.Trim();
        var preset = SilentInstallerCatalog.GetPreset(type);
        var arguments = string.IsNullOrWhiteSpace(custom) ? preset.Arguments : custom;
        if (string.IsNullOrWhiteSpace(arguments))
        {
            arguments = "/S";
        }

        if (string.IsNullOrWhiteSpace(custom) && type == InstallerType.Unknown)
        {
            context.Line("Installer type was not recognized; using /S. Test the switches if the installer opens a window.");
        }

        string fileName;
        string commandLine;
        if (extension == ".msi")
        {
            fileName = "msiexec.exe";
            commandLine = $"/i {Quote(installerPath)} {arguments}";
        }
        else
        {
            fileName = installerPath;
            commandLine = $"{Quote(installerPath)} {arguments}";
        }

        context.Line($"Detected {type.DisplayName()}; running: {SilentInstallerCatalog.BuildDisplayCommand(installerPath, arguments)}");
        context.Report(app, InstallStatus.Installing, $"Installing silently with {type.DisplayName()}.");

        var result = await _runner.RunCommandLineAsync(
            fileName,
            commandLine,
            new ProcessRunOptions
            {
                Timeout = TimeSpan.FromMinutes(60),
                WorkingDirectory = Path.GetDirectoryName(installerPath),
                OnLine = line =>
                {
                    var text = line.Text.Trim();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        context.Line(text);
                    }
                }
            },
            cancellationToken).ConfigureAwait(false);

        if (result.TimedOut)
        {
            context.Report(app, InstallStatus.Failed, "Installer timed out. Review the silent switches.");
            return InstallResult.Fail(
                "The installer timed out. It may have opened a window waiting for input. Test the silent switches in Add app.");
        }

        if (result.ExitCode == 0)
        {
            context.Report(app, InstallStatus.Success, "Installed silently.", 1);
            return InstallResult.Ok("Installed silently.");
        }

        if (result.ExitCode is 3010 or 1641)
        {
            context.Report(app, InstallStatus.RebootRequired, "Installed; restart required.", 1);
            return InstallResult.Reboot("Installed, but Windows needs a restart.");
        }

        if (result.ExitCode is 1602 or 1223)
        {
            return InstallResult.Fail("The installation was cancelled or administrator access was declined.");
        }

        var lastLines = result.CombinedOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .TakeLast(3);
        var detail = string.Join(' ', lastLines);
        if (detail.Length > 400)
        {
            detail = detail[..400];
        }

        var error = $"Installer exited with code {result.ExitCode}." +
                    (string.IsNullOrWhiteSpace(detail) ? string.Empty : " " + detail);
        context.Report(app, InstallStatus.Failed, error);
        return InstallResult.Fail(error, result.ExitCode);
    }

    private async Task<InstallResult> InstallMsixAsync(
        AppEntry app,
        string path,
        InstallContext context,
        CancellationToken cancellationToken)
    {
        context.Line("Registering MSIX package…");
        var script = $"Add-AppxPackage -Path '{path.Replace("'", "''")}' -ForceApplicationShutdown";
        var result = await _runner.RunAsync(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script],
            new ProcessRunOptions
            {
                Timeout = TimeSpan.FromMinutes(15),
                OnLine = line => { if (!string.IsNullOrWhiteSpace(line.Text)) context.Line(line.Text.Trim()); }
            },
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode == 0)
        {
            context.Report(app, InstallStatus.Success, "MSIX package registered.", 1);
            return InstallResult.Ok("MSIX package registered.");
        }

        var error = result.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim()
                    ?? "Add-AppxPackage failed.";
        context.Report(app, InstallStatus.Failed, error);
        return InstallResult.Fail(error, result.ExitCode);
    }

    private InstallResult ExtractPortable(AppEntry app, string archivePath, InstallContext context)
    {
        var destination = Path.Combine(_paths.Apps, FileUtil.Slug(app.Name));
        try
        {
            Directory.CreateDirectory(destination);
            context.Line($"Extracting portable app to {destination}");
            ZipFile.ExtractToDirectory(archivePath, destination, overwriteFiles: true);
            var executable = FindPortableExecutable(destination);
            var detail = executable is null ? $"Extracted to {destination}" : $"Extracted to {destination} ({Path.GetFileName(executable)})";
            context.Report(app, InstallStatus.Success, detail, 1);
            return InstallResult.Ok(detail);
        }
        catch (Exception ex)
        {
            _log.Write(LogLevel.Error, $"Could not extract portable app {app.Name}.", ex);
            context.Report(app, InstallStatus.Failed, "Could not extract the archive.");
            return InstallResult.Fail($"Could not extract the portable archive: {ex.Message}");
        }
    }

    private InstallResult CopyPortable(AppEntry app, string filePath, InstallContext context)
    {
        try
        {
            var destinationDirectory = Path.Combine(_paths.Apps, FileUtil.Slug(app.Name));
            Directory.CreateDirectory(destinationDirectory);
            var destination = Path.Combine(destinationDirectory, Path.GetFileName(filePath));
            File.Copy(filePath, destination, overwrite: true);
            context.Line($"Copied portable app to {destination}");
            context.Report(app, InstallStatus.Success, $"Portable app copied to {destination}", 1);
            return InstallResult.Ok($"Portable app copied to {destination}");
        }
        catch (Exception ex)
        {
            context.Report(app, InstallStatus.Failed, "Could not copy the portable executable.");
            return InstallResult.Fail($"Could not copy the portable executable: {ex.Message}");
        }
    }

    private async Task<InstallResult> ExtractAndRunInstallerAsync(
        AppEntry app,
        string archivePath,
        InstallContext context,
        CancellationToken cancellationToken)
    {
        var staging = Path.Combine(_paths.Cache, "extracted", Guid.NewGuid().ToString("n"));
        try
        {
            Directory.CreateDirectory(staging);
            context.Line("Extracting installer archive…");
            ZipFile.ExtractToDirectory(archivePath, staging, overwriteFiles: true);
            var installer = FindBundledInstaller(staging);
            if (installer is null)
            {
                return InstallResult.NeedsAction(
                    "The ZIP did not contain a recognizable setup.exe or .msi. Mark it as a portable archive in Add app, or install it manually.");
            }

            context.Line($"Found bundled installer {Path.GetFileName(installer)}.");
            var result = await RunInstallerAsync(app, installer, context, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex)
        {
            return InstallResult.Fail($"Could not unpack the installer archive: {ex.Message}");
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    private static string? FindBundledInstaller(string directory)
    {
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToArray();
        return files
            .Where(path => path.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(path =>
            {
                var name = Path.GetFileNameWithoutExtension(path);
                return name.Contains("setup", StringComparison.OrdinalIgnoreCase) ? 3
                    : name.Contains("install", StringComparison.OrdinalIgnoreCase) ? 2
                    : path.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            })
            .ThenBy(path => path.Length)
            .FirstOrDefault();
    }

    private static string? FindPortableExecutable(string directory)
    {
        return Directory.EnumerateFiles(directory, "*.exe", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileName(path).Contains("unins", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path.Count(c => c == Path.DirectorySeparatorChar))
            .ThenBy(path => path.Length)
            .FirstOrDefault();
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // The installer may still hold a file; staging is cleaned next time.
        }
    }

    private static string Quote(string value) => ElevationService.Quote(value);
}

public static class InstallerTypeNames
{
    public static string DisplayName(this InstallerType type) => type switch
    {
        InstallerType.InnoSetup => "Inno Setup",
        InstallerType.Nsis => "NSIS",
        InstallerType.Msi => "Windows Installer",
        InstallerType.InstallShield => "InstallShield",
        InstallerType.Squirrel => "Squirrel",
        InstallerType.WixBurn => "WiX Burn",
        InstallerType.Msix => "MSIX",
        InstallerType.PortableArchive => "portable archive",
        InstallerType.PortableExecutable => "portable executable",
        _ => "unknown installer"
    };
}
