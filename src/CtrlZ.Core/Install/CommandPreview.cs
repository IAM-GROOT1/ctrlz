// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

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
    /// <remarks>
    /// The approval store keeps hashes per profile, not per command slot, so an edited command
    /// cannot yet be told apart from one never approved: both show as Blocked, which fails safe.
    /// </remarks>
    public static CommandPreviewLine? ForApp(AppEntry app, int order, ProfileOrigin origin, bool approved)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.PostInstall is not { } command)
        {
            return null;
        }

        var hash = PostInstallApprovalStore.HashOf(command);
        var approvedHashes = approved ? new[] { hash } : Array.Empty<string>();

        var resolution = CommandStateResolver.Resolve(origin, approvedHashes, command);
        var allowed = CommandStateResolver.MayRun(resolution.State);
        var willRun = allowed && command.Enabled;

        var text = allowed && !command.Enabled
            ? OffInProfileText + (resolution.Risky ? " - Warning: risky pattern" : string.Empty)
            : resolution.Text;

        return new CommandPreviewLine(
            app.Id, app.Name, order, command.Describe(), resolution.State, resolution.Risky, willRun, text, hash);
    }

    /// <summary>Lines for every enabled app with a command, numbered like the install plan.</summary>
    public static IReadOnlyList<CommandPreviewLine> ForProfile(
        AppProfile profile, ProfileOrigin origin, Func<PostInstallCommand, bool> isApproved)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(isApproved);

        var lines = new List<CommandPreviewLine>();
        var order = 0;
        foreach (var app in profile.Apps.Where(app => app.Enabled))
        {
            order++;
            if (app.PostInstall is { } command &&
                ForApp(app, order, origin, isApproved(command)) is { } line)
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
        return ForProfile(profile, origin, command => approvals.IsApproved(profile.Id, command));
    }

    /// <summary>
    /// Lines numbered exactly like the install plan (dependency order). Apps the plan will not run
    /// have no line, because their command cannot run either.
    /// </summary>
    public static IReadOnlyList<CommandPreviewLine> ForPlan(
        Safety.InstallPlan plan, AppProfile profile, ProfileOrigin origin, Func<PostInstallCommand, bool> isApproved)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(isApproved);

        var lines = new List<CommandPreviewLine>();
        foreach (var item in plan.Items.Where(item => item.NotInstalledReason is null))
        {
            var app = profile.Apps.FirstOrDefault(candidate => candidate.Id == item.AppId);
            if (app?.PostInstall is { } command &&
                ForApp(app, item.Order, origin, isApproved(command)) is { } line)
            {
                lines.Add(line);
            }
        }

        return lines;
    }
}
