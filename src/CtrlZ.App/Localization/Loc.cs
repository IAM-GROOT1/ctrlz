// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Globalization;
using System.Resources;

namespace CtrlZ.App.Localization;

/// <summary>
/// The app's user-facing text. English lives in <c>Resources/Strings.resx</c>; a translation is a
/// <c>Strings.&lt;language&gt;.resx</c> next to it with the same keys. A key missing from a
/// translation falls back to English, so a partly translated language still shows every screen.
/// </summary>
public static class Loc
{
    private static readonly ResourceManager Resources =
        new("CtrlZ.App.Resources.Strings", typeof(Loc).Assembly);

    /// <summary>The text for <paramref name="key"/> in the current UI language.</summary>
    /// <remarks>An unknown key returns <c>[key]</c> so it is visible on screen; a test checks
    /// that every key used in XAML and code exists, so this should never ship.</remarks>
    public static string Get(string key) =>
        Resources.GetString(key, CultureInfo.CurrentUICulture) ?? $"[{key}]";

    /// <summary>The text for <paramref name="key"/> with <c>{0}</c>-style placeholders filled in.</summary>
    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);
}
