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

    // WPF's text layout ignores the Unicode isolate marks (U+2066 to U+2069) and the embedding marks
    // (U+202A to U+202E), as a test window in Windows Sandbox showed; only the plain direction marks
    // work, so these helpers use those.
    private const string LeftToRightMark = "‎";
    private const string RightToLeftMark = "‏";

    /// <summary>
    /// Keeps <paramref name="text"/> (a command, path or id) in left-to-right order inside a sentence
    /// in a right-to-left language, so punctuation at its ends stays with it instead of jumping to the
    /// other side. There it is wrapped in left-to-right marks, which take up no space on screen; in a
    /// left-to-right language the text is returned unchanged.
    /// </summary>
    public static string LeftToRight(string text) =>
        Current.TextInfo.IsRightToLeft ? LeftToRightMark + text + LeftToRightMark : text;

    /// <summary>
    /// Shows a name that did not come from CTRL+Z's translations (a profile, an app, a publisher,
    /// a winget id) in its own direction, whatever the language around it: the direction of its
    /// first letter decides, as the Unicode "first strong isolate" would if WPF supported it. In
    /// Arabic, "7-Zip" stays "7-Zip" and "(x64)" keeps its brackets, and a Latin profile name does
    /// not pull the app count next to it out of place. Text that already reads in the language's
    /// direction is returned unchanged.
    /// </summary>
    public static string Isolate(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var textIsRightToLeft = StartsRightToLeft(text);
        if (Current.TextInfo.IsRightToLeft)
        {
            return textIsRightToLeft
                ? RightToLeftMark + text + RightToLeftMark
                : LeftToRightMark + text + LeftToRightMark;
        }

        return textIsRightToLeft ? RightToLeftMark + text + RightToLeftMark : text;
    }

    /// <summary>True when the first letter is Hebrew, Arabic or another right-to-left script.</summary>
    private static bool StartsRightToLeft(string text)
    {
        foreach (var c in text)
        {
            if (c is (>= '֐' and <= 'ࣿ') or (>= 'יִ' and <= '﷿') or (>= 'ﹰ' and <= '﻿'))
            {
                return true;
            }

            if (char.IsLetter(c))
            {
                return false;
            }
        }

        // Digits and symbols only, such as a version: left to right.
        return false;
    }

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
