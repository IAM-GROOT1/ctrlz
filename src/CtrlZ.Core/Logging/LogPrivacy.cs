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

    /// <summary>
    /// Formats an exception for the log as <c>TypeName: redacted message</c>. The type keeps
    /// the line debuggable; the message is kept but with any path replaced. Never pass the
    /// exception object to the logger directly, because the logger appends
    /// <c>Exception.ToString()</c>, which embeds the message and the stack unchanged.
    /// </summary>
    public static string Describe(Exception? exception)
    {
        if (exception is null)
        {
            return string.Empty;
        }

        var type = exception.GetType().Name;
        var message = RedactPaths(exception.Message);
        return string.IsNullOrWhiteSpace(message) ? type : type + ": " + message;
    }

    [GeneratedRegex(@"[A-Za-z]:\\[^""'\r\n]*|\\\\[^\\\s""']+\\[^""'\r\n]*")]
    private static partial Regex WindowsPath();
}
