// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Models;

namespace CtrlZ.Core.Updates;

/// <summary>A downloadable asset attached to a release.</summary>
public sealed record UpdateAsset(string Name, string DownloadUrl, long Size, string? Sha256);

/// <summary>A published GitHub release, reduced to what the updater needs.</summary>
public sealed record ReleaseInfo(
    string TagName,
    SemanticVersion Version,
    string Title,
    string? Notes,
    string HtmlUrl,
    DateTimeOffset? PublishedAt,
    bool IsPreRelease,
    IReadOnlyList<UpdateAsset> Assets);

public enum UpdateFailureKind
{
    Offline,
    RateLimited,
    NotFound,
    Malformed,
    Unknown
}

public sealed class UpdateCheckException : Exception
{
    public UpdateCheckException(UpdateFailureKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    public UpdateFailureKind Kind { get; }

    /// <summary>Message safe to show a user; never includes a raw exception body.</summary>
    public string FriendlyMessage => Kind switch
    {
        UpdateFailureKind.Offline => "Could not reach GitHub. Check your connection and try again.",
        UpdateFailureKind.RateLimited => "GitHub's hourly request limit was reached. Try again later.",
        UpdateFailureKind.NotFound => "No releases were found for CTRL+Z yet.",
        UpdateFailureKind.Malformed => "GitHub returned a response CTRL+Z could not read.",
        _ => "The update check failed. Try again later."
    };
}

/// <summary>Result of asking GitHub for the newest release on a channel.</summary>
public sealed record UpdateCheck(UpdateChannel Channel, SemanticVersion CurrentVersion, ReleaseInfo? Latest)
{
    /// <summary>True only when the newest release is strictly newer: never a downgrade.</summary>
    public bool UpdateAvailable => Latest is not null && Latest.Version.IsNewerThan(CurrentVersion);

    public static UpdateCheck UpToDate(UpdateChannel channel, SemanticVersion current) => new(channel, current, null);
}
