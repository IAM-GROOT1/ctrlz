// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Models;

namespace CtrlZ.Core.Install;

/// <summary>What will happen to one command. Nothing here runs anything.</summary>
public enum CommandDecision
{
    /// <summary>Will run.</summary>
    Run = 0,

    /// <summary>The command is switched off in the profile.</summary>
    Disabled = 1,

    /// <summary>The profile did not come from this machine, so nothing runs until it is reviewed.</summary>
    BlockedUntrusted = 2,

    /// <summary>The command text changed after it was approved, so it needs reviewing again.</summary>
    NeedsReview = 3,

    /// <summary>Will run, but only after a strong confirmation because of a risky pattern.</summary>
    RiskyNeedsConfirmation = 4
}

/// <summary>A command plus the verdict for it, so the preview and the runner agree.</summary>
public sealed record CommandVerdict(
    AppEntry App,
    PostInstallCommand Command,
    CommandDecision Decision,
    string Reason,
    IReadOnlyList<string> Risks)
{
    /// <summary>True only when the command may actually be executed.</summary>
    public bool MayRun => Decision is CommandDecision.Run or CommandDecision.RiskyNeedsConfirmation;
}

/// <summary>
/// The single place that decides whether a post-install command may run. It reads the profile's
/// origin from <see cref="ProfileOriginStore"/> and the reviewed hashes from
/// <see cref="PostInstallApprovalStore"/>, never a flag inside the profile file.
/// </summary>
public sealed class PostInstallCommandGate
{
    private readonly ProfileOriginStore _origins;
    private readonly PostInstallApprovalStore _approvals;

    public PostInstallCommandGate(ProfileOriginStore origins, PostInstallApprovalStore approvals)
    {
        _origins = origins;
        _approvals = approvals;
    }

    public IReadOnlyList<CommandVerdict> Evaluate(AppProfile profile)
    {
        var verdicts = new List<CommandVerdict>();
        var origin = _origins.Get(profile.Id);

        foreach (var app in profile.Apps.Where(item => item.PostInstall is not null))
        {
            verdicts.Add(EvaluateCommand(profile, app, app.PostInstall!, origin));
        }

        return verdicts;
    }

    public CommandVerdict EvaluateCommand(AppProfile profile, AppEntry app, PostInstallCommand command, ProfileOrigin? origin = null)
    {
        var profileOrigin = origin ?? _origins.Get(profile.Id);
        var risks = PostInstallCommandRisk.Scan(command);
        var approved = _approvals.IsApproved(profile.Id, command);

        // Order matters: an untrusted profile is blocked first, so no stored flag can rescue it.
        if (profileOrigin != ProfileOrigin.Local && !approved)
        {
            var reason = profileOrigin == ProfileOrigin.Imported
                ? "This profile came from outside this PC. Review the command to enable it."
                : "This profile has no local origin record, so its commands stay disabled.";

            return new CommandVerdict(app, command, CommandDecision.BlockedUntrusted, reason, risks);
        }

        if (profileOrigin != ProfileOrigin.Local && approved && !command.Enabled)
        {
            // Approved but switched off in the file: still disabled.
            return new CommandVerdict(app, command, CommandDecision.Disabled, "Disabled in the profile.", risks);
        }

        if (profileOrigin == ProfileOrigin.Local && !command.Enabled)
        {
            return new CommandVerdict(app, command, CommandDecision.Disabled, "Disabled in the profile.", risks);
        }

        if (profileOrigin == ProfileOrigin.Imported && approved)
        {
            return risks.Count > 0
                ? new CommandVerdict(app, command, CommandDecision.RiskyNeedsConfirmation,
                    "Approved for this profile, but it matches a risky pattern.", risks)
                : new CommandVerdict(app, command, CommandDecision.Run, "Reviewed and approved for this profile.", risks);
        }

        if (risks.Count > 0)
        {
            return new CommandVerdict(app, command, CommandDecision.RiskyNeedsConfirmation,
                "Matches a risky pattern; needs a strong confirmation every run.", risks);
        }

        return new CommandVerdict(app, command, CommandDecision.Run, "Local profile command.", risks);
    }
}
