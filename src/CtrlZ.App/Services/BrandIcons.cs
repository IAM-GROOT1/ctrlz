// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace CtrlZ.App.Services;

/// <summary>
/// Brand logos for well-known apps, bundled with CTRL+Z (Simple Icons, CC0 1.0; see
/// <c>build/brand-icons</c>). Used when an app is not installed on this PC, so it has no icon of its
/// own. Nothing is downloaded.
/// </summary>
public static partial class BrandIcons
{
    public sealed record Brand(string Title, Brush Background, Brush Glyph, Geometry Shape);

    private sealed record Index(IReadOnlyDictionary<string, Brand> ByPackage, IReadOnlyDictionary<string, Brand> ByName);

    private static readonly Lazy<Index> Data = new(Load);

    /// <summary>How many logos are bundled (for tests).</summary>
    public static int Count => Data.Value.ByName.Values.Distinct().Count();

    /// <summary>The logo for an app, by its winget id first and then by its name, or null.</summary>
    public static Brand? Find(string? packageId, string? name)
    {
        var index = Data.Value;
        if (!string.IsNullOrWhiteSpace(packageId) &&
            index.ByPackage.TryGetValue(packageId.Trim().ToLowerInvariant(), out var byId))
        {
            return byId;
        }

        foreach (var key in NameKeys(name))
        {
            if (index.ByName.TryGetValue(key, out var byName))
            {
                return byName;
            }
        }

        return null;
    }

    /// <summary>
    /// Lookup keys for a display name, most exact first: as is, without anything in brackets or
    /// version numbers ("7-Zip 24.09 (x64)" → "7zip"), and without a leading publisher word
    /// ("Mozilla Firefox" → "firefox").
    /// </summary>
    public static IEnumerable<string> NameKeys(string? name, bool dropFirstWord = true)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var plain = Brackets().Replace(name, " ");
        plain = VersionWords().Replace(plain, " ");
        var words = plain.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var candidates = new List<string> { name, plain };
        if (dropFirstWord && words.Length > 1)
        {
            candidates.Add(string.Join(' ', words.Skip(1)));
        }

        foreach (var candidate in candidates)
        {
            var key = Normalize(candidate);
            if (key.Length > 1 && seen.Add(key))
            {
                yield return key;
            }
        }
    }

    /// <summary>Lower-case letters, digits and '+' only (the same rule the generator uses).</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormKD).ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9' or '+')
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static Index Load()
    {
        var byPackage = new Dictionary<string, Brand>(StringComparer.Ordinal);
        var byName = new Dictionary<string, Brand>(StringComparer.Ordinal);
        try
        {
            using var stream = typeof(BrandIcons).Assembly.GetManifestResourceStream("CtrlZ.App.Resources.brand-icons.json");
            if (stream is null)
            {
                return new Index(byPackage, byName);
            }

            using var document = JsonDocument.Parse(stream);
            foreach (var item in document.RootElement.GetProperty("icons").EnumerateArray())
            {
                var brand = Create(
                    item.GetProperty("t").GetString() ?? string.Empty,
                    item.GetProperty("c").GetString() ?? "777777",
                    item.GetProperty("p").GetString() ?? string.Empty);
                if (brand is null)
                {
                    continue;
                }

                foreach (var id in item.GetProperty("w").EnumerateArray())
                {
                    byPackage.TryAdd(id.GetString() ?? string.Empty, brand);
                }

                foreach (var name in item.GetProperty("n").EnumerateArray())
                {
                    byName.TryAdd(name.GetString() ?? string.Empty, brand);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or KeyNotFoundException)
        {
            // A broken bundle only costs the logos; the coloured initials still show.
        }

        return new Index(byPackage, byName);
    }

    private static Brand? Create(string title, string hex, string pathData)
    {
        try
        {
            var shape = Geometry.Parse(pathData);
            shape.Freeze();
            var color = Color.FromRgb(
                byte.Parse(hex.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(hex.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(hex.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            var background = new SolidColorBrush(color);
            background.Freeze();
            // A light brand colour (yellow, white) gets a dark logo so it stays visible.
            var luminance = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255;
            var glyph = luminance > 0.62 ? new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)) : new SolidColorBrush(Colors.White);
            glyph.Freeze();
            return new Brand(title, background, glyph, shape);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"(?<=^|\s)(v?\d+(\.\d+)*[a-z]?|x64|x86|arm64|64-bit|32-bit)(?=\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex VersionWords();
}
