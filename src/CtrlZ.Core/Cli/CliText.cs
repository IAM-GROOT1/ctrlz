// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Globalization;
using System.Text;

namespace CtrlZ.Core.Cli;

/// <summary>
/// Text from a profile file is printed to a terminal and then approved with --allow-commands, so it
/// must look exactly like what it is. Control characters (line breaks, ANSI escapes) and
/// direction overrides could hide or fake parts of a command; they are shown as visible escapes,
/// and a command containing any of them is never approved from the command line.
/// </summary>
public static class CliText
{
    public static bool HasHidden(string? text) =>
        !string.IsNullOrEmpty(text) && text.Any(IsHidden);

    public static string Safe(string? text)
    {
        if (string.IsNullOrEmpty(text) || !HasHidden(text))
        {
            return text ?? string.Empty;
        }

        var builder = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            if (IsHidden(c))
            {
                builder.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static bool IsHidden(char c) =>
        char.IsControl(c) ||
        CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format ||
        c == (char)0x2028 || c == (char)0x2029;
}
