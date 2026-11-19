// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

namespace CtrlZ.Core.Safety;

/// <summary>
/// Builds diagnostics text for the safety checks. Everything here is derived from counts, so
/// the log and the diagnostics copy can never carry the name of a file or folder from a
/// user's personal folders.
/// </summary>
public static class SafetyDiagnostics
{
    /// <summary>
    /// One line summarising a readiness scan. Counts only, by construction: the number of
    /// folders that could not be read is included so an access-denied scan is debuggable
    /// without ever naming the folder.
    /// </summary>
    public static string Summary(ReadinessReport report, int unreadableFolders = 0)
    {
        var text = $"Readiness check: {report.PassCount} pass, {report.WarningCount} warning, " +
                   $"{report.FailCount} fail";

        if (unreadableFolders > 0)
        {
            text += $", {unreadableFolders} folder{(unreadableFolders == 1 ? string.Empty : "s")} unreadable";
        }

        return text + ".";
    }

    /// <summary>One line summarising a drift comparison. Counts only.</summary>
    public static string DriftSummary(DriftReport report) =>
        $"Drift check: {report.Missing.Count} missing, {report.Extra.Count} extra, " +
        $"{report.Outdated.Count} outdated, {report.IgnoredCount} ignored.";
}
