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
    public static bool HasHidden(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        for (var i = 0; i < text.Length;)
        {
            var (hidden, length, _) = Next(text, i);
            if (hidden)
            {
                return true;
            }

            i += length;
        }

        return false;
    }

    public static string Safe(string? text)
    {
        if (string.IsNullOrEmpty(text) || !HasHidden(text))
        {
            return text ?? string.Empty;
        }

        var builder = new StringBuilder(text.Length + 16);
        for (var i = 0; i < text.Length;)
        {
            var (hidden, length, value) = Next(text, i);
            if (!hidden)
            {
                builder.Append(text, i, length);
            }
            else if (value > 0xFFFF)
            {
                builder.Append("\\U").Append(value.ToString("X8", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append("\\u").Append(value.ToString("X4", CultureInfo.InvariantCulture));
            }

            i += length;
        }

        return builder.ToString();
    }

    /// <summary>
    /// The character at <paramref name="index"/>, read as a whole Unicode scalar so characters
    /// beyond U+FFFF are checked too: the invisible tag characters (U+E0000 to U+E007F) are Format
    /// characters that a UTF-16 check misses. A lone surrogate is hidden as well.
    /// </summary>
    private static (bool Hidden, int Length, int Value) Next(string text, int index)
    {
        if (Rune.DecodeFromUtf16(text.AsSpan(index), out var rune, out var length) != System.Buffers.OperationStatus.Done)
        {
            return (true, 1, text[index]);
        }

        var hidden = Rune.IsControl(rune) ||
                     Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format ||
                     rune.Value is 0x2028 or 0x2029;
        return (hidden, length, rune.Value);
    }
}
