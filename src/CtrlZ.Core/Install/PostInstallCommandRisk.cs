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

    // Each match has a time limit, so a very long argument list in an imported profile cannot stall
    // the review screen. A pattern that runs out of time counts as a hit: the scan fails closed.
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private static Rule R(Func<string> label, string pattern) =>
        new(label, new Regex("(?i)" + pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant, MatchTimeout));

    // Program names are matched with or without ".exe", a full path and a closing quote, so
    // "C:\Windows\System32\reg.exe add ..." is caught as well as "reg add ...". Labels are looked
    // up on every scan so they always follow the current language.
    private const string Exe = @"(\.exe)?""?\s+";
    private const string Hive = @"\b(hklm|hkcu|hkcr|hku|hkey_[a-z_]+)(:|\\)";

    private static readonly Rule[] Rules =
    {
        R(() => CoreText.Get("PostInstallCommandRisk_DownloadsAndRunsRemoteContent"),
            @"\b(curl|wget|invoke-webrequest|iwr|invoke-restmethod|irm|downloadstring|downloadfile|start-bitstransfer|bitsadmin)\b" +
            @"|\bcertutil\b.*[-/](urlcache|verifyctl)\b" +
            @"|\b(msiexec|mshta)\b.*\bhttps?://|\brundll32\b.*\burl\.dll\b"),
        R(() => CoreText.Get("PostInstallCommandRisk_RunsAnEncodedOrHidden"),
            // PowerShell accepts any unambiguous prefix of a parameter: -e, -ec, -enc, -win and so on.
            @"(-encodedcommand|-enc\s|\s-w[a-z]*\s+hidden\b)|\b(powershell|pwsh)\b.*\s-(e|ec|en[a-z]*)\s"),
        R(() => CoreText.Get("PostInstallCommandRisk_DeletesOrWritesOutsideYour"),
            @"\b(remove-item|del|erase|rmdir|rd|format|takeown|icacls)\b.*\b[a-z]:\\" +
            @"|\bvssadmin" + Exe + @"(delete|resize)\b|\bwmic\b.*\bshadowcopy\b.*\bdelete\b|\bwin32_shadowcopy\b.*\bdelete\b" +
            @"|\bwbadmin" + Exe + @"delete\b"),
        R(() => CoreText.Get("PostInstallCommandRisk_ChangesTheRegistryServicesOr"),
            @"\b(reg" + Exe + @"(add|delete|copy|load|restore)|regedit|new-service|set-service|schtasks|new-scheduledtask|register-scheduledtask)\b" +
            @"|\bsc" + Exe + @"(\\\\\S+\s+)?(create|config|delete|sdset|failure)\b" +
            @"|\b(set|new|remove|rename)-itemproperty\b|\b(new-item|remove-item|set-item|sp|ni|ri|si)\b.*" + Hive +
            @"|\bmicrosoft\.win32\.registry\b.*\b(setvalue|createsubkey|deletesubkey|deletevalue)\b"),
        R(() => CoreText.Get("PostInstallCommandRisk_ChangesStartupEntries"), @"(\\run\b|\\runonce\b|\\startup(\\|""|\s|$))"),
        R(() => CoreText.Get("PostInstallCommandRisk_WeakensSecuritySettings"),
            @"\b(set-mppreference|add-mppreference|disableantispyware|disableantivirus|defender\s+disable)\b" +
            @"|\bnetsh\b.*\b(advfirewall|firewall)\b.*\s(set|add|delete)(\s|$)" +
            @"|\b(set-netfirewallprofile|new-netfirewallrule|set-netfirewallrule|disable-netfirewallrule|new-localuser|add-localgroupmember)\b" +
            @"|\bnet1?" + Exe + @"(user|localgroup)\b.*\/add\b|\bbcdedit\b" +
            @"|\bset-executionpolicy\b.*\b(bypass|unrestricted)\b")
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
            if (Matches(rule.Pattern, text) && rule.Label() is var label && !hits.Contains(label, StringComparer.Ordinal))
            {
                hits.Add(label);
            }
        }

        return hits;
    }

    private static bool Matches(Regex pattern, string text)
    {
        try
        {
            return pattern.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            // Too long to check in time: treat it as risky rather than as safe.
            return true;
        }
    }
}
