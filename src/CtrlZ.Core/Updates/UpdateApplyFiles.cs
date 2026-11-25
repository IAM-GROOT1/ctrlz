// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Text.Json;
using System.Text.Json.Serialization;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Localization;

namespace CtrlZ.Core.Updates;

/// <summary>Written by the app before it exits, read by the update helper.</summary>
public sealed class UpdateApplyRequest
{
    public string Kind { get; set; } = nameof(UpdateInstallKind.Portable);
    public string DownloadedFile { get; set; } = string.Empty;
    public string TargetExe { get; set; } = string.Empty;
    public string? ExpectedSha256 { get; set; }
    public string? Version { get; set; }
    public int WaitForProcessId { get; set; }

    /// <summary>Portable only: the downloaded <c>ctrlz-&lt;version&gt;.exe</c>, or null when not updating it.</summary>
    public string? CompanionDownloadedFile { get; set; }

    /// <summary>Portable only: the <c>ctrlz.exe</c> next to the app that the companion replaces.</summary>
    public string? CompanionTargetExe { get; set; }

    /// <summary>The SHA-256 the companion must match, from the release's SHA256SUMS.txt.</summary>
    public string? CompanionExpectedSha256 { get; set; }
}

/// <summary>Written by the helper; the restarted app shows the message when it failed.</summary>
public sealed class UpdateApplyResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? Version { get; set; }

    public static UpdateApplyResult Ok(string? version) => new() { Success = true, Version = version };

    /// <summary>
    /// True when the new version failed to start and the previous one was put back. The app then
    /// skips <see cref="Version"/> like "Skip this version", so the same build is not offered again.
    /// </summary>
    public bool RolledBack { get; set; }

    /// <summary>
    /// The CoreStrings key of <see cref="Message"/>, so the app shows it in the user's language while
    /// the log keeps the English <see cref="Message"/>. Null for messages with no translation.
    /// </summary>
    public string? MessageKey { get; set; }

    /// <summary>Values for the placeholders of <see cref="MessageKey"/>.</summary>
    public string[]? MessageArgs { get; set; }

    public static UpdateApplyResult Failed(string message, string? messageKey = null, params string[] messageArgs) =>
        new() { Success = false, Message = message, MessageKey = messageKey, MessageArgs = messageArgs };

    /// <summary>The helper's own message keys; any other key read from the file is ignored.</summary>
    private static readonly IReadOnlyDictionary<string, Func<object?[], string>> Translations =
        new Dictionary<string, Func<object?[], string>>(StringComparer.Ordinal)
        {
            ["UpdateApply_DownloadMissing"] = args => CoreText.Format("UpdateApply_DownloadMissing", args),
            ["UpdateApply_NotPutInPlace"] = args => CoreText.Format("UpdateApply_NotPutInPlace", args),
            ["UpdateApply_NotRecorded"] = args => CoreText.Format("UpdateApply_NotRecorded", args),
            ["UpdateApply_InstallerMissing"] = args => CoreText.Format("UpdateApply_InstallerMissing", args),
            ["UpdateApply_InstallerNotStarted"] = args => CoreText.Format("UpdateApply_InstallerNotStarted", args),
            ["UpdateApply_InstallerError"] = args => CoreText.Format("UpdateApply_InstallerError", args),
            ["UpdateApply_RestoreFailed"] = args => CoreText.Format("UpdateApply_RestoreFailed", args),
            ["UpdateApply_NotStartedRestored"] = args => CoreText.Format("UpdateApply_NotStartedRestored", args),
            ["UpdateApply_ClosedRolledBack"] = args => CoreText.Format("UpdateApply_ClosedRolledBack", args),
            ["UpdateApply_CouldNotApply"] = args => CoreText.Format("UpdateApply_CouldNotApply", args),
        };

    /// <summary>True when <paramref name="key"/> is one of the helper's own message keys.</summary>
    public static bool IsKnownMessageKey(string key) => Translations.ContainsKey(key);

    public static UpdateApplyResult RolledBackFrom(string message, string? version, string? messageKey = null) =>
        new() { Success = false, Message = message, Version = version, RolledBack = true, MessageKey = messageKey };

    /// <summary>
    /// The message for the user: the translation when there is a key, otherwise the English text.
    /// The result file can be edited, so only the helper's own keys are used, and a translation that
    /// does not fit the values falls back to the English text.
    /// </summary>
    [JsonIgnore]
    public string? DisplayMessage
    {
        get
        {
            if (MessageKey is null || !Translations.TryGetValue(MessageKey, out var translate))
            {
                return Message;
            }

            try
            {
                return translate((MessageArgs ?? []).Cast<object?>().ToArray());
            }
            catch (FormatException)
            {
                return Message;
            }
        }
    }
}

/// <summary>Small JSON handoff files between the app and the update helper.</summary>
public static class UpdateApplyFiles
{
    public const string ResultFileName = "update-result.json";

    public static void WriteRequest(string path, UpdateApplyRequest request) =>
        WriteJson(path, request);

    public static UpdateApplyRequest? ReadRequest(string path) => ReadJson<UpdateApplyRequest>(path);

    public static void WriteResult(string path, UpdateApplyResult result) =>
        WriteJson(path, result);

    public static UpdateApplyResult? ReadResult(string path) => ReadJson<UpdateApplyResult>(path);

    public static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Losing the marker only costs one error message.
        }
    }

    private static void WriteJson<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonDefaults.Indented));
    }

    private static T? ReadJson<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonDefaults.Compact);
        }
        catch
        {
            return null;
        }
    }
}
