// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Localization;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Install;

/// <summary>One post-install command as the preview, the dry run and the CLI print it.</summary>
public sealed record CommandPreviewLine(
    string AppId,
    string AppName,
    int Order,
    string CommandText,
    CommandRunState State,
    bool Risky,
    bool WillRun,
    string StateText,
    string Hash);

/// <summary>
/// Builds the command lines shown before an install. It only describes: it never runs anything and
/// never writes an approval. <see cref="CommandPreviewLine.WillRun"/> must always agree with
/// <see cref="PostInstallCommandGate"/>, which a test checks for every combination.
/// </summary>
public static class CommandPreview
{
    public const string OffInProfileText = "Off in profile - will not run";

    /// <summary>The line for one app, or null when the app has no command.</summary>
    public static CommandPreviewLine? ForApp(AppEntry app, int order, ProfileOrigin origin, bool approved) =>
        ForApp(app, order, origin, approved ? ApprovalState.Current : ApprovalState.None);

    /// <summary>The line for one app from its slot's approval state, or null when it has no command.</summary>
    public static CommandPreviewLine? ForApp(AppEntry app, int order, ProfileOrigin origin, ApprovalState approval)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.PostInstall is not { } command)
        {
            return null;
        }

        var hash = PostInstallApprovalStore.HashOf(command);

        // The resolver only needs to know whether this slot holds the current hash, another hash
        // (edited since approval) or nothing.
        var approvedHashes = approval switch
        {
            ApprovalState.Current => new[] { hash },
            ApprovalState.Outdated => new[] { "outdated:" + hash },
            _ => Array.Empty<string>()
        };

        var resolution = CommandStateResolver.Resolve(origin, approvedHashes, command);
        var allowed = CommandStateResolver.MayRun(resolution.State);
        var willRun = allowed && command.Enabled;

        var text = allowed && !command.Enabled
            ? OffInProfileText + (resolution.Risky ? CoreText.Get("CommandPreview_WarningRiskyPattern") : string.Empty)
            : resolution.Text;

        return new CommandPreviewLine(
            app.Id, app.Name, order, command.Describe(), resolution.State, resolution.Risky, willRun, text, hash);
    }

    /// <summary>Lines for every enabled app with a command, numbered in profile order.</summary>
    public static IReadOnlyList<CommandPreviewLine> ForProfile(
        AppProfile profile, ProfileOrigin origin, Func<PostInstallCommand, bool> isApproved)
    {
        ArgumentNullException.ThrowIfNull(isApproved);
        return ForProfile(profile, origin, (_, command) => isApproved(command) ? ApprovalState.Current : ApprovalState.None);
    }

    /// <summary>Lines for every enabled app with a command, from each slot's approval state.</summary>
    public static IReadOnlyList<CommandPreviewLine> ForProfile(
        AppProfile profile, ProfileOrigin origin, Func<AppEntry, PostInstallCommand, ApprovalState> approvalOf)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(approvalOf);

        var lines = new List<CommandPreviewLine>();
        var order = 0;
        foreach (var app in profile.Apps.Where(app => app.Enabled))
        {
            order++;
            if (app.PostInstall is { } command &&
                ForApp(app, order, origin, approvalOf(app, command)) is { } line)
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    /// <summary>Reads approvals for this profile from the store; read-only.</summary>
    public static IReadOnlyList<CommandPreviewLine> ForProfile(
        AppProfile profile, ProfileOrigin origin, PostInstallApprovalStore approvals)
    {
        ArgumentNullException.ThrowIfNull(approvals);
        return ForProfile(profile, origin, (app, command) => approvals.StateOf(profile.Id, app.Id, command));
    }

    /// <summary>Plan-ordered lines reading approvals from the store; read-only.</summary>
    public static IReadOnlyList<CommandPreviewLine> ForPlan(
        Safety.InstallPlan plan, AppProfile profile, ProfileOrigin origin, PostInstallApprovalStore approvals)
    {
        ArgumentNullException.ThrowIfNull(approvals);
        return ForPlan(plan, profile, origin, (app, command) => approvals.StateOf(profile.Id, app.Id, command));
    }

    /// <summary>Plan-ordered lines from a plain approved/not-approved answer.</summary>
    public static IReadOnlyList<CommandPreviewLine> ForPlan(
        Safety.InstallPlan plan, AppProfile profile, ProfileOrigin origin, Func<PostInstallCommand, bool> isApproved)
    {
        ArgumentNullException.ThrowIfNull(isApproved);
        return ForPlan(plan, profile, origin, (_, command) => isApproved(command) ? ApprovalState.Current : ApprovalState.None);
    }

    /// <summary>
    /// Lines numbered exactly like the install plan (dependency order). Apps the plan will not run
    /// have no line, because their command cannot run either.
    /// </summary>
    public static IReadOnlyList<CommandPreviewLine> ForPlan(
        Safety.InstallPlan plan, AppProfile profile, ProfileOrigin origin, Func<AppEntry, PostInstallCommand, ApprovalState> approvalOf)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(approvalOf);

        var lines = new List<CommandPreviewLine>();
        foreach (var item in plan.Items.Where(item => item.NotInstalledReason is null))
        {
            var app = profile.Apps.FirstOrDefault(candidate => candidate.Id == item.AppId);
            if (app?.PostInstall is { } command &&
                ForApp(app, item.Order, origin, approvalOf(app, command)) is { } line)
            {
                lines.Add(line);
            }
        }

        return lines;
    }
}
