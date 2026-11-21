// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

namespace CtrlZ.Core.Updates;

/// <summary>
/// Plain-language text for the ways an update can fail. Kept here so the wording is testable and
/// so a timeout can never be reported as a user cancellation.
/// </summary>
public static class UpdateFailureMessages
{
    /// <summary>The user pressed Cancel or Escape.</summary>
    public const string UserCancelled = "The update was cancelled. Nothing was changed.";

    /// <summary>The request timed out, or the connection dropped. Nothing was written.</summary>
    public const string TimedOut =
        "The update could not be downloaded in time. Nothing was changed. Try again later.";

    /// <summary>Anonymous GitHub quota exhausted. No mention of tokens: the feed never sends one.</summary>
    public const string RateLimited = "GitHub is limiting update checks right now. Try again in about an hour.";

    /// <summary>Rate limited, with the reset time when the response told us.</summary>
    public static string RateLimitedAt(DateTimeOffset? resetUtc) =>
        resetUtc is null ? RateLimited : $"GitHub is limiting update checks right now. Try again after {resetUtc:HH:mm} UTC.";

    /// <summary>
    /// Chooses the message for a cancelled update. A cancellation the user asked for stays a
    /// cancellation; anything else (a timeout, a dropped connection) is reported as a timeout.
    /// </summary>
    public static string ForCancellation(bool userRequested) =>
        userRequested ? UserCancelled : TimedOut;
}
