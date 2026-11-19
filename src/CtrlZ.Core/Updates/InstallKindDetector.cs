// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using Microsoft.Win32;

namespace CtrlZ.Core.Updates;

/// <summary>Reads the install folder recorded by the Inno Setup uninstall entry.</summary>
public interface IInstallLocationProbe
{
    /// <summary>The recorded install folder, or null when CTRL+Z is not installed.</summary>
    string? GetInstallLocation();
}

/// <summary>
/// Per-user Inno Setup uninstall key. <c>PrivilegesRequired=lowest</c> means CTRL+Z installs
/// under HKCU, so this is the authoritative "was this copy installed?" signal.
/// </summary>
public sealed class RegistryInstallLocationProbe : IInstallLocationProbe
{
    // Must match the AppId in installer/CTRL+Z.iss (Inno appends "_is1" for per-user installs).
    public const string UninstallKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{0941C8F1-8BBA-4F3E-822A-7DDB6598644A}_is1";

    public string? GetInstallLocation()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
            return key?.GetValue("InstallLocation") as string;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Decides whether the running copy came from the installer or is a portable exe, which
/// determines whether an update downloads Setup.exe or the portable exe.
/// </summary>
public static class InstallKindDetector
{
    public static UpdateInstallKind Detect(string? exePath, string? installLocation, bool portableFlagPresent)
    {
        // An explicit portable.flag is the user's intent and always wins.
        if (portableFlagPresent)
        {
            return UpdateInstallKind.Portable;
        }

        if (string.IsNullOrWhiteSpace(exePath) || string.IsNullOrWhiteSpace(installLocation))
        {
            return UpdateInstallKind.Portable;
        }

        try
        {
            var exeDirectory = Path.GetDirectoryName(Path.GetFullPath(exePath));
            if (string.IsNullOrWhiteSpace(exeDirectory))
            {
                return UpdateInstallKind.Portable;
            }

            var installed = Path.GetFullPath(installLocation)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            var running = exeDirectory
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return string.Equals(running, installed, StringComparison.OrdinalIgnoreCase)
                ? UpdateInstallKind.Installed
                : UpdateInstallKind.Portable;
        }
        catch
        {
            return UpdateInstallKind.Portable;
        }
    }

    /// <summary>Detects using the real registry probe and the portable.flag next to the exe.</summary>
    public static UpdateInstallKind Detect(string? exePath, IInstallLocationProbe probe)
    {
        var exeDirectory = string.IsNullOrWhiteSpace(exePath)
            ? null
            : Path.GetDirectoryName(exePath);

        var portableFlag = exeDirectory is not null &&
            File.Exists(Path.Combine(exeDirectory, "portable.flag"));

        return Detect(exePath, probe.GetInstallLocation(), portableFlag);
    }
}
