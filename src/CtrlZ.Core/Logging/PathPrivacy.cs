// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Text.RegularExpressions;

namespace CtrlZ.Core.Logging;

/// <summary>
/// Keeps the Windows account out of logs and saved state. Personal folders are written as
/// environment-variable tokens (<c>%LOCALAPPDATA%\CTRL+Z\...</c>), which is reversible, so saved
/// state still works after loading; logs additionally replace the account and PC names.
/// </summary>
public sealed class PathPrivacy
{
    private readonly IReadOnlyList<(string Token, string Path)> _roots;
    private readonly string _userName;
    private readonly string _machineName;

    /// <param name="roots">Token and folder pairs; the longest folder is matched first.</param>
    public PathPrivacy(IEnumerable<(string Token, string Path)> roots, string userName, string machineName)
    {
        _roots = roots
            .Where(root => !string.IsNullOrWhiteSpace(root.Path))
            .Select(root => (root.Token, Path: root.Path.TrimEnd('\\', '/')))
            .Where(root => root.Path.Length > 3)
            .OrderByDescending(root => root.Path.Length)
            .ToList();
        _userName = userName ?? string.Empty;
        _machineName = machineName ?? string.Empty;
    }

    /// <summary>This user's folders: local and roaming AppData, Temp and the profile itself.</summary>
    public static PathPrivacy Current { get; } = new(
        [
            ("%LOCALAPPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
            ("%APPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
            ("%TEMP%", Path.GetTempPath()),
            ("%USERPROFILE%", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
        ],
        Environment.UserName,
        Environment.MachineName);

    /// <summary>Replaces each personal folder with its token. Handles JSON-escaped text too.</summary>
    public string Tokenize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        foreach (var (token, path) in _roots)
        {
            text = Replace(text, path.Replace("\\", "\\\\"), token);
            text = Replace(text, path, token);
        }

        return text;
    }

    /// <summary>Turns tokens back into this user's folders, in plain or JSON-escaped text.</summary>
    public string Expand(string? text, bool json = false)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        foreach (var (token, path) in _roots)
        {
            text = text.Replace(token, json ? path.Replace("\\", "\\\\") : path, StringComparison.OrdinalIgnoreCase);
        }

        return text;
    }

    /// <summary>For a log line: folders become tokens, then the account and PC names are hidden.</summary>
    public string ForLog(string? text)
    {
        var result = Tokenize(text);
        result = HideWord(result, _userName, "<user>");
        return HideWord(result, _machineName, "<pc>");
    }

    private static string Replace(string text, string value, string token) =>
        text.Replace(value, token, StringComparison.OrdinalIgnoreCase);

    private static string HideWord(string text, string word, string replacement)
    {
        if (word.Length < 3)
        {
            return text;
        }

        return Regex.Replace(text, @"(?<![\p{L}\p{N}])" + Regex.Escape(word) + @"(?![\p{L}\p{N}])", replacement,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
