// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Globalization;

namespace CtrlZ.App.Localization;

/// <summary>A language the Settings page offers. An empty code means "follow Windows".</summary>
public sealed record LanguageOption(string Code, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// The languages CTRL+Z ships and how the chosen one is applied. The language is set once at
/// startup, before any window is built, so a change in Settings takes effect after a restart.
/// </summary>
public static class AppLanguage
{
    /// <summary>
    /// Languages with a translation in Resources. English is the built-in text; a test checks this
    /// list against the Strings.&lt;code&gt;.resx files so the two cannot drift apart.
    /// </summary>
    public static readonly IReadOnlyList<string> Supported = ["en", "de"];

    /// <summary>Uses <paramref name="code"/> for all text from now on; empty or unknown follows Windows.</summary>
    public static void Apply(string? code)
    {
        if (Normalize(code) is not { } supported)
        {
            return;
        }

        var culture = CultureInfo.GetCultureInfo(supported);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    /// <summary>A supported language code, or null for "follow Windows".</summary>
    public static string? Normalize(string? code) =>
        string.IsNullOrWhiteSpace(code)
            ? null
            : Supported.FirstOrDefault(s => string.Equals(s, code.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The choices for Settings: "Windows default" first, then each language in its own name.</summary>
    public static IReadOnlyList<LanguageOption> Options()
    {
        var options = new List<LanguageOption> { new(string.Empty, Loc.Get("Settings_LanguageWindowsDefault")) };
        foreach (var code in Supported)
        {
            var native = CultureInfo.GetCultureInfo(code).NativeName;
            options.Add(new LanguageOption(code, char.ToUpper(native[0], CultureInfo.GetCultureInfo(code)) + native[1..]));
        }

        return options;
    }
}
