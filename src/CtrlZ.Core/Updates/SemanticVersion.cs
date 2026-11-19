// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Globalization;

namespace CtrlZ.Core.Updates;

/// <summary>
/// Semantic Versioning 2.0 value with the full precedence rules, including pre-release
/// ordering. Build metadata is parsed and preserved but ignored for precedence, as the
/// specification requires. A leading <c>v</c> is accepted because git tags carry one.
/// </summary>
public sealed class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    private readonly string[] _preRelease;

    private SemanticVersion(int major, int minor, int patch, string[] preRelease, string? buildMetadata)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        _preRelease = preRelease;
        BuildMetadata = buildMetadata;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public IReadOnlyList<string> PreRelease => _preRelease;
    public string? BuildMetadata { get; }
    public bool IsPreRelease => _preRelease.Length > 0;

    public static SemanticVersion Create(int major, int minor, int patch) =>
        new(major, minor, patch, [], null);

    public static SemanticVersion Create(int major, int minor, int patch, string preRelease) =>
        TryParse($"{major}.{minor}.{patch}-{preRelease}", out var parsed) && parsed is not null
            ? parsed
            : throw new FormatException($"'{preRelease}' is not a valid pre-release label.");

    public static bool TryParse(string? text, out SemanticVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        if (value.Length == 0)
        {
            return false;
        }

        string? buildMetadata = null;
        var plus = value.IndexOf('+');
        if (plus >= 0)
        {
            buildMetadata = value[(plus + 1)..];
            value = value[..plus];
            if (!IsValidIdentifierList(buildMetadata, allowLeadingZeroes: true))
            {
                return false;
            }
        }

        string? preRelease = null;
        var dash = value.IndexOf('-');
        if (dash >= 0)
        {
            preRelease = value[(dash + 1)..];
            value = value[..dash];
        }

        var core = value.Split('.');
        if (core.Length != 3)
        {
            return false;
        }

        if (!TryParseCoreNumber(core[0], out var major) ||
            !TryParseCoreNumber(core[1], out var minor) ||
            !TryParseCoreNumber(core[2], out var patch))
        {
            return false;
        }

        var identifiers = Array.Empty<string>();
        if (preRelease is not null)
        {
            if (!IsValidIdentifierList(preRelease, allowLeadingZeroes: false))
            {
                return false;
            }

            identifiers = preRelease.Split('.');
        }

        version = new SemanticVersion(major, minor, patch, identifiers, buildMetadata);
        return true;
    }

    public static SemanticVersion Parse(string text) =>
        TryParse(text, out var version) && version is not null
            ? version
            : throw new FormatException($"'{text}' is not a valid semantic version.");

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var result = Major.CompareTo(other.Major);
        if (result != 0)
        {
            return result;
        }

        result = Minor.CompareTo(other.Minor);
        if (result != 0)
        {
            return result;
        }

        result = Patch.CompareTo(other.Patch);
        if (result != 0)
        {
            return result;
        }

        return ComparePreRelease(_preRelease, other._preRelease);
    }

    /// <summary>True when this version is strictly newer than <paramref name="other"/>.</summary>
    public bool IsNewerThan(SemanticVersion? other) => CompareTo(other) > 0;

    public bool Equals(SemanticVersion? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => Equals(obj as SemanticVersion);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Major);
        hash.Add(Minor);
        hash.Add(Patch);
        foreach (var identifier in _preRelease)
        {
            hash.Add(identifier, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    public override string ToString()
    {
        var value = string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
        if (_preRelease.Length > 0)
        {
            value += "-" + string.Join('.', _preRelease);
        }

        if (!string.IsNullOrEmpty(BuildMetadata))
        {
            value += "+" + BuildMetadata;
        }

        return value;
    }

    /// <summary>The numeric <c>major.minor.patch</c> form, valid for <see cref="Version"/>.</summary>
    public string ToNumericString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    /// <summary>Four-part numeric version for <c>AssemblyVersion</c>/<c>FileVersion</c>.</summary>
    public Version ToAssemblyVersion() => new(Major, Minor, Patch, 0);

    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;
    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;
    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;
    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;

    private static int ComparePreRelease(string[] left, string[] right)
    {
        if (left.Length == 0 && right.Length == 0)
        {
            return 0;
        }

        // A version without a pre-release suffix has higher precedence.
        if (left.Length == 0)
        {
            return 1;
        }

        if (right.Length == 0)
        {
            return -1;
        }

        var shared = Math.Min(left.Length, right.Length);
        for (var i = 0; i < shared; i++)
        {
            var result = CompareIdentifier(left[i], right[i]);
            if (result != 0)
            {
                return result;
            }
        }

        // All shared identifiers are equal: the longer set wins.
        return left.Length.CompareTo(right.Length);
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = IsNumeric(left);
        var rightNumeric = IsNumeric(right);

        if (leftNumeric && rightNumeric)
        {
            // Numeric identifiers carry no leading zeroes, so length orders them and
            // avoids overflow on very large values.
            var byLength = left.Length.CompareTo(right.Length);
            return byLength != 0 ? byLength : string.CompareOrdinal(left, right);
        }

        // Numeric identifiers always have lower precedence than alphanumeric ones.
        if (leftNumeric)
        {
            return -1;
        }

        if (rightNumeric)
        {
            return 1;
        }

        return string.CompareOrdinal(left, right);
    }

    private static bool TryParseCoreNumber(string text, out int value)
    {
        value = 0;
        if (text.Length == 0 || (text.Length > 1 && text[0] == '0'))
        {
            return false;
        }

        foreach (var character in text)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool IsValidIdentifierList(string text, bool allowLeadingZeroes)
    {
        if (text.Length == 0)
        {
            return false;
        }

        foreach (var part in text.Split('.'))
        {
            if (part.Length == 0)
            {
                return false;
            }

            foreach (var character in part)
            {
                var allowed = character is >= '0' and <= '9'
                    or >= 'a' and <= 'z'
                    or >= 'A' and <= 'Z'
                    or '-';
                if (!allowed)
                {
                    return false;
                }
            }

            if (!allowLeadingZeroes && IsNumeric(part) && part.Length > 1 && part[0] == '0')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsNumeric(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }
}
