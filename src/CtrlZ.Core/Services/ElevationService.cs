// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Localization;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace CtrlZ.Core.Services;

/// <summary>
/// Elevation is deliberately explicit. CTRL+Z runs asInvoker by default and only
/// relaunches itself through UAC when the user starts an install.
/// </summary>
public static class ElevationService
{
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool TryRelaunchElevated(IEnumerable<string> arguments, out string? error)
    {
        error = null;
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            error = CoreText.Get("ElevationService_CTRLZCouldNotDetermine");
            return false;
        }

        try
        {
            var info = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
                Arguments = string.Join(' ', arguments.Select(Quote))
            };
            Process.Start(info);
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            error = CoreText.Get("ElevationService_AdministratorAccessWasDeclined");
            return false;
        }
        catch (Exception ex)
        {
            error = CoreText.Format("ElevationService_CouldNotRestartWithAdministrator", ex.Message);
            return false;
        }
    }

    /// <summary>Quotes a single Windows command-line argument.</summary>
    public static string Quote(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        if (!value.Any(c => char.IsWhiteSpace(c) || c == '"'))
        {
            return value;
        }

        var builder = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in value)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
                backslashes = 0;
                continue;
            }

            builder.Append('\\', backslashes);
            backslashes = 0;
            builder.Append(c);
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }
}
