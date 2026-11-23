// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Globalization;

namespace CtrlZ.Core.Localization;

/// <summary>
/// The plural form a whole number takes in a language, following the Unicode CLDR categories
/// (Zero, One, Two, Few, Many, Other) for the languages that need more than "one or other".
/// Only whole counts are used in the app, so fractional rules are left out.
/// </summary>
public static class PluralRules
{
    public static string Category(CultureInfo culture, long count)
    {
        var n = Math.Abs(count);
        var mod10 = n % 10;
        var mod100 = n % 100;
        return culture.TwoLetterISOLanguageName switch
        {
            // No plural forms.
            "ja" or "zh" or "ko" or "vi" or "th" or "id" or "ms" => "Other",

            // 0 and 1 are singular.
            "fr" or "pt" when culture.Name != "pt-PT" => n is 0 or 1 ? "One" : (n != 0 && n % 1_000_000 == 0 ? "Many" : "Other"),

            "ru" or "uk" or "be" => mod10 == 1 && mod100 != 11 ? "One"
                : mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14 ? "Few"
                : "Many",

            "pl" => n == 1 ? "One"
                : mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14 ? "Few"
                : "Many",

            "cs" or "sk" => n == 1 ? "One" : n is >= 2 and <= 4 ? "Few" : "Other",

            "ar" => n == 0 ? "Zero"
                : n == 1 ? "One"
                : n == 2 ? "Two"
                : mod100 is >= 3 and <= 10 ? "Few"
                : mod100 is >= 11 and <= 99 ? "Many"
                : "Other",

            "he" => n == 1 ? "One" : n == 2 ? "Two" : "Other",

            _ => n == 1 ? "One" : "Other"
        };
    }
}
