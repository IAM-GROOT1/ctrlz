// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Management;
using CtrlZ.Core.Logging;

namespace CtrlZ.Core.Services;

/// <summary>
/// Best-effort VSS snapshot used only when a profile file is locked. Requires an elevated
/// process; when unavailable, the backup reports a clear "close the browser" error instead
/// of silently creating an incomplete archive.
/// </summary>
public sealed class VolumeShadowCopySession : IDisposable
{
    private readonly string _shadowId;
    private readonly string _deviceObject;
    private readonly string _driveRoot;
    private bool _disposed;

    private VolumeShadowCopySession(string shadowId, string deviceObject, string driveRoot)
    {
        _shadowId = shadowId;
        _deviceObject = deviceObject.TrimEnd('\\');
        _driveRoot = driveRoot;
    }

    public string DeviceObject => _deviceObject;

    public static bool TryCreate(string sourcePath, IAppLog? log, out VolumeShadowCopySession? session, out string error)
    {
        session = null;
        error = string.Empty;
        if (!ElevationService.IsElevated)
        {
            error = "Volume Shadow Copy needs administrator access.";
            return false;
        }

        var driveRoot = Path.GetPathRoot(Path.GetFullPath(sourcePath));
        if (string.IsNullOrWhiteSpace(driveRoot) || driveRoot.StartsWith(@"\\", StringComparison.Ordinal))
        {
            error = "Volume Shadow Copy only supports local drives.";
            return false;
        }

        try
        {
            using var shadowClass = new ManagementClass("Win32_ShadowCopy");
            using var inParameters = shadowClass.GetMethodParameters("Create");
            inParameters["Volume"] = driveRoot;
            inParameters["Context"] = "ClientAccessible";
            using var outParameters = shadowClass.InvokeMethod("Create", inParameters, null);
            var shadowId = outParameters?["ShadowID"]?.ToString();
            if (string.IsNullOrWhiteSpace(shadowId))
            {
                error = "Windows did not return a shadow copy id.";
                return false;
            }

            string? deviceObject = null;
            using (var searcher = new ManagementObjectSearcher(
                       $"SELECT DeviceObject FROM Win32_ShadowCopy WHERE ID = '{shadowId.Replace("'", "''")}'"))
            {
                foreach (ManagementObject item in searcher.Get())
                {
                    deviceObject = item["DeviceObject"]?.ToString();
                    item.Dispose();
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(deviceObject))
            {
                error = "The shadow copy could not be mapped to a device path.";
                return false;
            }

            session = new VolumeShadowCopySession(shadowId, deviceObject, driveRoot);
            log?.Write(LogLevel.Info, $"Created volume shadow copy {shadowId} for {driveRoot}.");
            return true;
        }
        catch (Exception ex)
        {
            error = $"Volume Shadow Copy failed: {ex.Message}";
            log?.Write(LogLevel.Warning, error, ex);
            return false;
        }
    }

    public string MapPath(string originalPath)
    {
        var full = Path.GetFullPath(originalPath);
        if (!full.StartsWith(_driveRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The path is not on the shadow copy volume.", nameof(originalPath));
        }

        var relative = full[_driveRoot.Length..].TrimStart(Path.DirectorySeparatorChar);
        return Path.Combine(_deviceObject, relative);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            using var shadow = new ManagementObject(
                new ManagementScope(@"\\.\root\cimv2"),
                new ManagementPath($"Win32_ShadowCopy.ID='{_shadowId.Replace("'", "''")}'"),
                null);
            shadow.Delete();
        }
        catch
        {
            // Windows eventually cleans orphaned client-accessible snapshots.
        }
    }
}
