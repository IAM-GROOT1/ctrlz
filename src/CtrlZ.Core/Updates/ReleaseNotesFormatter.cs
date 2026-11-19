// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Text;
using System.Text.RegularExpressions;

namespace CtrlZ.Core.Updates;

/// <summary>
/// Converts a GitHub release body (Markdown) into plain text for display.
/// <para>
/// Everything produced here is inert text: it is shown in a WPF <c>TextBlock</c>, never fed
/// to a browser or an HTML renderer. Raw HTML tags in the release body are stripped rather
/// than interpreted, so a malicious release note cannot load remote content or run script.
/// </para>
/// </summary>
public static partial class ReleaseNotesFormatter
{
    public const int DefaultMaxLength = 4000;

    [GeneratedRegex(@"<[^<>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"(?m)^\s{0,3}#{1,6}\s*", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingMarker();

    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex Image();

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)\s]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex Link();

    [GeneratedRegex(@"(?m)^\s*```[^\n]*$", RegexOptions.CultureInvariant)]
    private static partial Regex CodeFence();

    [GeneratedRegex(@"(?m)^\s*[-*+]\s+", RegexOptions.CultureInvariant)]
    private static partial Regex BulletMarker();

    [GeneratedRegex(@"(?m)^\s*>\s?", RegexOptions.CultureInvariant)]
    private static partial Regex QuoteMarker();

    [GeneratedRegex(@"[ \t]+$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingSpace();

    [GeneratedRegex(@"\n{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex BlankLines();

    /// <summary>
    /// Returns display-ready plain text. Never throws, whatever the release body contains.
    /// </summary>
    public static string ToPlainText(string? markdown, int maxLength = DefaultMaxLength)
    {
        if (maxLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLength));
        }

        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var text = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

        text = CodeFence().Replace(text, string.Empty);
        text = HtmlTag().Replace(text, string.Empty);
        text = Image().Replace(text, "$1");
        text = Link().Replace(text, "$1 ($2)");
        text = HeadingMarker().Replace(text, string.Empty);
        text = QuoteMarker().Replace(text, string.Empty);
        text = BulletMarker().Replace(text, "\u2022 ");

        // Emphasis and inline-code markers are noise once formatting is gone.
        text = text
            .Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("__", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal);

        text = TrailingSpace().Replace(text, string.Empty);
        text = BlankLines().Replace(text, "\n\n");
        text = StripControlCharacters(text).Trim();

        return text.Length <= maxLength ? text : Truncate(text, maxLength);
    }

    private static string StripControlCharacters(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            // Keep newlines and tabs; drop other control characters, including NUL.
            if (character is '\n' or '\t' || !char.IsControl(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static string Truncate(string text, int maxLength)
    {
        var cut = text.LastIndexOf(' ', Math.Min(maxLength, text.Length - 1));
        if (cut < maxLength / 2)
        {
            cut = maxLength;
        }

        return text[..cut].TrimEnd() + "\u2026";
    }
}
