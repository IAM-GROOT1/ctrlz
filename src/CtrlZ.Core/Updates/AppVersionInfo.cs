// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Reflection;

namespace CtrlZ.Core.Updates;

/// <summary>
/// Resolves the running application's semantic version.
/// <para>
/// The build stamps <see cref="AssemblyInformationalVersionAttribute"/> from the git tag, so
/// that attribute is the single source of truth. <c>AssemblyVersion</c> cannot carry a
/// pre-release suffix (it is always four numeric parts), which is why it is only a fallback.
/// </para>
/// </summary>
public static class AppVersionInfo
{
    /// <summary>Version of the currently running application.</summary>
    public static SemanticVersion Current { get; } = FromAssembly(Assembly.GetEntryAssembly());

    /// <summary>Display form, for example <c>1.2.0</c> or <c>1.3.0-beta.1</c>.</summary>
    public static string Display => Current.ToString();

    public static SemanticVersion FromAssembly(Assembly? assembly)
    {
        if (assembly is null)
        {
            return SemanticVersion.Create(0, 0, 0);
        }

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        return Parse(informational, assembly.GetName().Version);
    }

    /// <summary>
    /// Parses an informational version, tolerating the <c>+buildmetadata</c> suffix that SDK
    /// builds append. Falls back to the numeric assembly version when the value is missing
    /// or malformed.
    /// </summary>
    public static SemanticVersion Parse(string? informationalVersion, Version? fallback = null)
    {
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var value = informationalVersion.Trim();
            var plus = value.IndexOf('+');
            if (plus >= 0)
            {
                value = value[..plus];
            }

            if (SemanticVersion.TryParse(value, out var parsed) && parsed is not null)
            {
                return parsed;
            }
        }

        if (fallback is not null)
        {
            return SemanticVersion.Create(
                Math.Max(0, fallback.Major),
                Math.Max(0, fallback.Minor),
                Math.Max(0, fallback.Build));
        }

        return SemanticVersion.Create(0, 0, 0);
    }
}
