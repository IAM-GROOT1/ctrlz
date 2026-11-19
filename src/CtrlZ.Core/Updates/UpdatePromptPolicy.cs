// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Models;

namespace CtrlZ.Core.Updates;

/// <summary>Snapshot of the persisted settings a prompt decision depends on.</summary>
public sealed record UpdatePromptState(
    bool AutoCheck,
    string? SkippedVersion,
    DateTimeOffset? RemindAfterUtc)
{
    public static UpdatePromptState FromSettings(AppSettings settings) =>
        new(settings.AutoCheckForUpdates, settings.SkippedUpdateVersion, settings.UpdateRemindAfterUtc);
}

public enum UpdatePromptDecision
{
    /// <summary>Offer the release.</summary>
    Show,

    /// <summary>The user asked to skip this version (or a newer one).</summary>
    Skipped,

    /// <summary>"Remind me later" is still in effect.</summary>
    RemindPending,

    /// <summary>Automatic checks are off, so only a manual check may prompt.</summary>
    AutomaticChecksOff
}

/// <summary>
/// Decides whether a newer release should be offered. Pure and clock-injected so the
/// skip/remind rules are unit-tested without a UI.
/// </summary>
public static class UpdatePromptPolicy
{
    public static UpdatePromptDecision Decide(
        UpdatePromptState state,
        SemanticVersion releaseVersion,
        DateTimeOffset now,
        bool manual)
    {
        // A manual check always overrides "automatic checks are off".
        if (!manual && !state.AutoCheck)
        {
            return UpdatePromptDecision.AutomaticChecksOff;
        }

        // A skipped version stays skipped until a strictly newer one appears.
        if (SemanticVersion.TryParse(state.SkippedVersion, out var skipped) &&
            skipped is not null &&
            skipped.CompareTo(releaseVersion) >= 0)
        {
            return UpdatePromptDecision.Skipped;
        }

        // "Remind me later" only suppresses the automatic prompt; a manual check is explicit.
        if (!manual && state.RemindAfterUtc is { } remindAfter && now < remindAfter)
        {
            return UpdatePromptDecision.RemindPending;
        }

        return UpdatePromptDecision.Show;
    }
}
