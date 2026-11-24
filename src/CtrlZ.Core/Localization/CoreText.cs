// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Globalization;
using System.Resources;

namespace CtrlZ.Core.Localization;

/// <summary>
/// User-facing text produced by the core library: readiness results, install progress, preview
/// warnings and the command safety messages. English lives in <c>Resources/CoreStrings.resx</c>;
/// a translation is a <c>CoreStrings.&lt;language&gt;.resx</c> with the same keys, and a missing key
/// falls back to English. Log lines and exception details for developers stay English.
/// </summary>
public static class CoreText
{
    private static readonly ResourceManager Resources =
        new("CtrlZ.Core.Resources.CoreStrings", typeof(CoreText).Assembly);

    /// <summary>
    /// The language chosen for the app's text, or null to use the thread's UI culture. It is a
    /// plain static rather than the thread culture because .NET restores the culture when an
    /// async method returns, so a language set during async startup would not stick.
    /// </summary>
    public static CultureInfo? Culture { get; set; }

    /// <summary>The language text is read in right now.</summary>
    public static CultureInfo Current => Culture ?? CultureInfo.CurrentUICulture;

    /// <summary>The text for <paramref name="key"/> in the current UI language, or <c>[key]</c>.</summary>
    public static string Get(string key) =>
        Resources.GetString(key, Current) ?? $"[{key}]";

    /// <summary>The text for <paramref name="key"/> with <c>{0}</c>-style placeholders filled in.</summary>
    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    /// <summary>
    /// Keeps <paramref name="text"/> (a command, path or id) in left-to-right order inside a sentence
    /// in a right-to-left language, which could otherwise reorder the characters around it. There it
    /// wraps the text in the Unicode left-to-right isolate marks, which take up no space on screen;
    /// in a left-to-right language the text is returned unchanged.
    /// </summary>
    public static string LeftToRight(string text) =>
        Current.TextInfo.IsRightToLeft ? "⁦" + text + "⁩" : text;

    /// <summary>Count-dependent text; see <see cref="PluralRules"/>. The count is <c>{0}</c>.</summary>
    public static string Plural(string key, long count, params object?[] args)
    {
        var culture = Current;
        var text = Resources.GetString($"{key}_{PluralRules.Category(culture, count)}", culture)
                   ?? Resources.GetString($"{key}_Other", culture)
                   ?? $"[{key}]";
        return string.Format(CultureInfo.CurrentCulture, text, [count, .. args]);
    }
}
