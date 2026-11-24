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
    private sealed record Rule(Func<string> Label, Regex Pattern);

    private static Rule R(Func<string> label, string pattern) => new(label, new Regex(pattern, RegexOptions.Compiled));

    // Program names are matched with or without ".exe", a full path and a closing quote, so
    // "C:\Windows\System32\reg.exe add ..." is caught as well as "reg add ...". Labels are looked
    // up on every scan so they always follow the current language.
    private const string Exe = @"(\.exe)?""?\s+";

    private static readonly Rule[] Rules =
    {
        R(() => CoreText.Get("PostInstallCommandRisk_DownloadsAndRunsRemoteContent"),
            @"(?i)\b(curl|wget|invoke-webrequest|iwr|invoke-restmethod|irm|downloadstring|downloadfile|start-bitstransfer|bitsadmin)\b" +
            @"|(?i)\bcertutil" + Exe + @".*-urlcache\b"),
        R(() => CoreText.Get("PostInstallCommandRisk_RunsAnEncodedOrHidden"), @"(?i)(-encodedcommand|-enc\s|-windowstyle\s+hidden|-w\s+hidden)"),
        R(() => CoreText.Get("PostInstallCommandRisk_DeletesOrWritesOutsideYour"),
            @"(?i)\b(remove-item|del|erase|rmdir|rd|format|takeown|icacls)\b.*\b[a-z]:\\" +
            @"|(?i)\bvssadmin" + Exe + @"(delete|resize)\b|(?i)\bwmic\b.*\bshadowcopy\b.*\bdelete\b"),
        R(() => CoreText.Get("PostInstallCommandRisk_ChangesTheRegistryServicesOr"),
            @"(?i)\b(reg" + Exe + @"(add|delete|copy|load|restore)|regedit|sc" + Exe + @"(create|config|delete|sdset|failure)|new-service|set-service|schtasks|new-scheduledtask|register-scheduledtask)\b" +
            @"|(?i)\b(set|new|remove|rename)-itemproperty\b|(?i)\b(new-item|remove-item|set-item)\b.*\b(hklm|hkcu|hkcr|hku|hkey_[a-z_]+):?"),
        R(() => CoreText.Get("PostInstallCommandRisk_ChangesStartupEntries"), @"(?i)(\\run\b|\\runonce\b|\\startup\\)"),
        R(() => CoreText.Get("PostInstallCommandRisk_WeakensSecuritySettings"),
            @"(?i)\b(set-mppreference|add-mppreference|disableantispyware|disableantivirus|defender\s+disable)\b" +
            @"|(?i)\bnetsh" + Exe + @"(advfirewall|firewall)\b.*\b(set|add|delete)\b|(?i)\bset-netfirewallprofile\b" +
            @"|(?i)\bnet" + Exe + @"(user|localgroup)\b.*\/add\b|(?i)\bbcdedit\b" +
            @"|(?i)\bset-executionpolicy\b.*\b(bypass|unrestricted)\b")
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
            if (rule.Pattern.IsMatch(text) && rule.Label() is var label && !hits.Contains(label, StringComparer.Ordinal))
            {
                hits.Add(label);
            }
        }

        return hits;
    }
}
