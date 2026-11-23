// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Localization;
using System.Text.RegularExpressions;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Install;

/// <summary>
/// Heuristic scan for command shapes that deserve a strong warning. It is deliberately blunt and
/// will produce false positives: the UI says so, and the result only ever adds a confirmation, it
/// never silently allows something.
/// </summary>
public static class PostInstallCommandRisk
{
    private sealed record Rule(string Label, Regex Pattern);

    private static Rule R(string label, string pattern) => new(label, new Regex(pattern, RegexOptions.Compiled));

    private static readonly Rule[] Rules =
    {
        R(CoreText.Get("PostInstallCommandRisk_DownloadsAndRunsRemoteContent"), @"(?i)\b(curl|wget|invoke-webrequest|iwr|downloadstring|start-bitstransfer|bitsadmin)\b"),
        R(CoreText.Get("PostInstallCommandRisk_RunsAnEncodedOrHidden"), @"(?i)(-encodedcommand|-enc\s|-windowstyle\s+hidden|-w\s+hidden)"),
        R(CoreText.Get("PostInstallCommandRisk_DeletesOrWritesOutsideYour"), @"(?i)\b(remove-item|del|erase|rmdir|rd|format|takeown|icacls)\b.*\b[a-z]:\\"),
        // reg/sc are matched with or without ".exe", a full path and closing quote, so
        // "C:\Windows\System32\reg.exe add ..." is caught as well as "reg add ...".
        R(CoreText.Get("PostInstallCommandRisk_ChangesTheRegistryServicesOr"), @"(?i)\b(reg(\.exe)?""?\s+(add|delete)|regedit|sc(\.exe)?""?\s+(create|config|delete)|new-service|set-service|schtasks|new-scheduledtask)\b"),
        R(CoreText.Get("PostInstallCommandRisk_ChangesStartupEntries"), @"(?i)(\\run\b|\\runonce\b|\\startup\\)"),
        R(CoreText.Get("PostInstallCommandRisk_WeakensSecuritySettings"), @"(?i)\b(set-mppreference|disableantispyware|disableantivirus|defender\s+disable)\b")
    };

    /// <summary>Risky patterns found in the command, as plain-language labels.</summary>
    public static IReadOnlyList<string> Scan(PostInstallCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.Executable))
        {
            return Array.Empty<string>();
        }

        var text = command.Describe();
        var hits = new List<string>();

        foreach (var rule in Rules)
        {
            if (rule.Pattern.IsMatch(text) && !hits.Contains(rule.Label, StringComparer.Ordinal))
            {
                hits.Add(rule.Label);
            }
        }

        return hits;
    }
}
