// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Diagnostics;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Services;

public sealed record BrowserProcessInfo(int ProcessId, string Name, string? ExecutablePath, string? WindowTitle);

/// <summary>
/// Detects and closes browser processes. Matching is path-first because Helium and Chromium
/// can both use chrome.exe; closing Chrome because Helium is selected would be unacceptable.
/// </summary>
public sealed class BrowserProcessService
{
    private readonly IAppLog _log;

    public BrowserProcessService(IAppLog log)
    {
        _log = log;
    }

    public IReadOnlyList<BrowserProcessInfo> FindRunning(DetectedBrowser browser)
    {
        var definitions = browser.Definition;
        var knownExecutables = ChromiumBrowserCatalog.ExpandPaths(definitions.ExecutableCandidates)
            .Where(File.Exists)
            .ToArray();
        if (!string.IsNullOrWhiteSpace(browser.ExecutablePath))
        {
            knownExecutables = knownExecutables.Append(browser.ExecutablePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        var processNames = definitions.ProcessNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var running = new List<BrowserProcessInfo>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (!processNames.Contains(process.ProcessName))
                {
                    continue;
                }

                string? path = null;
                try { path = process.MainModule?.FileName; } catch { }
                var matches = path is not null
                    ? MatchesExecutable(path, knownExecutables)
                    : knownExecutables.Length == 0 || !IsAmbiguousProcessName(process.ProcessName);
                if (!matches)
                {
                    continue;
                }

                running.Add(new BrowserProcessInfo(process.Id, process.ProcessName, path, process.MainWindowTitle));
            }
            catch
            {
                // A process can exit while we inspect it.
            }
            finally
            {
                process.Dispose();
            }
        }

        return running;
    }

    public bool IsRunning(DetectedBrowser browser) => FindRunning(browser).Count > 0;

    public async Task<bool> CloseAsync(DetectedBrowser browser, bool force, CancellationToken cancellationToken = default)
    {
        var processes = FindRunning(browser);
        if (processes.Count == 0)
        {
            return true;
        }

        _log.Write(LogLevel.Info, $"Closing {browser.DisplayName}: {string.Join(", ", processes.Select(p => p.Name))}");
        foreach (var info in processes)
        {
            try
            {
                using var process = Process.GetProcessById(info.ProcessId);
                if (!process.HasExited)
                {
                    process.CloseMainWindow();
                }
            }
            catch
            {
                // Process already gone.
            }
        }

        for (var i = 0; i < 16; i++)
        {
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            if (FindRunning(browser).Count == 0)
            {
                return true;
            }
        }

        if (!force)
        {
            return false;
        }

        foreach (var info in FindRunning(browser))
        {
            try
            {
                using var process = Process.GetProcessById(info.ProcessId);
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch
            {
                // Best effort; the next backup attempt will report remaining locks.
            }
        }

        await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        return FindRunning(browser).Count == 0;
    }

    private static bool MatchesExecutable(string path, string[] knownExecutables)
    {
        if (knownExecutables.Length == 0)
        {
            return true;
        }

        foreach (var executable in knownExecutables)
        {
            if (path.Equals(executable, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var directory = Path.GetDirectoryName(executable);
            if (!string.IsNullOrWhiteSpace(directory) &&
                path.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAmbiguousProcessName(string processName) =>
        processName.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("browser", StringComparison.OrdinalIgnoreCase);
}
