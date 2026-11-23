// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Localization;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Install;

/// <summary>What may happen to a command, decided from plain values only.</summary>
public enum CommandRunState
{
    /// <summary>The profile is trusted here: the command runs after install.</summary>
    Enabled = 0,

    /// <summary>The exact text was reviewed and approved on this machine.</summary>
    Approved = 1,

    /// <summary>It was approved once, then edited: it needs reviewing again.</summary>
    NeedsReview = 2,

    /// <summary>The profile came from outside and this command was never approved.</summary>
    Blocked = 3
}

/// <summary>The state plus everything the preview needs to explain it.</summary>
public sealed record CommandResolution(CommandRunState State, bool Risky, string Text);

/// <summary>
/// Pure decision function for a post-install command: no file, store or UI access, so it can be
/// tested exhaustively and reused by the preview, the review screen and the CLI.
/// </summary>
public static class CommandStateResolver
{
    /// <summary>Decides the state from the profile origin, the approved hashes and the command itself.</summary>
    public static CommandResolution Resolve(
        ProfileOrigin origin,
        IReadOnlyCollection<string> approvedHashes,
        PostInstallCommand command)
    {
        ArgumentNullException.ThrowIfNull(approvedHashes);
        ArgumentNullException.ThrowIfNull(command);

        var risky = PostInstallCommandRisk.Scan(command).Count > 0;
        var currentHash = PostInstallApprovalStore.HashOf(command);

        CommandRunState state;
        if (origin == ProfileOrigin.Local)
        {
            state = CommandRunState.Enabled;
        }
        else if (approvedHashes.Contains(currentHash, StringComparer.Ordinal))
        {
            state = CommandRunState.Approved;
        }
        else if (approvedHashes.Count > 0)
        {
            // An approval exists for this slot but not for this text: it was edited afterwards.
            state = CommandRunState.NeedsReview;
        }
        else
        {
            state = CommandRunState.Blocked;
        }

        return new CommandResolution(state, risky, Describe(state, risky));
    }

    /// <summary>Only a trusted or explicitly approved command may ever run.</summary>
    public static bool MayRun(CommandRunState state) =>
        state is CommandRunState.Enabled or CommandRunState.Approved;

    /// <summary>The sentence shown next to a command.</summary>
    public static string Describe(CommandRunState state, bool risky)
    {
        var text = state switch
        {
            CommandRunState.Enabled => CoreText.Get("CommandStateResolver_RunsAfterInstall"),
            CommandRunState.Approved => CoreText.Get("CommandStateResolver_ApprovedRunsAfterInstall"),
            CommandRunState.NeedsReview => CoreText.Get("CommandStateResolver_EditedSinceApprovalNeedsReview"),
            _ => CoreText.Get("CommandStateResolver_BlockedImportedProfileReviewRequired")
        };

        return risky ? text + CoreText.Get("CommandStateResolver_WarningRiskyPattern") : text;
    }
}
