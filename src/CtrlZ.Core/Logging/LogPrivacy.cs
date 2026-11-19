// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Text.RegularExpressions;

namespace CtrlZ.Core.Logging;

/// <summary>
/// Helpers for keeping machine-specific paths out of the log. Some framework exceptions embed
/// the path they failed on in their message, so anything that logs a message derived from an
/// exception goes through <see cref="RedactPaths"/> first.
/// </summary>
public static partial class LogPrivacy
{
    /// <summary>Replaces drive-qualified and UNC paths with <c>&lt;path&gt;</c>.</summary>
    public static string RedactPaths(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return WindowsPath().Replace(text, "<path>");
    }

    [GeneratedRegex(@"[A-Za-z]:\\[^""'\r\n]*|\\\\[^\\\s""']+\\[^""'\r\n]*")]
    private static partial Regex WindowsPath();
}
