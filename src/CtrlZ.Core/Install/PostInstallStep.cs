// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Install;

/// <summary>What happened to one app's post-install command.</summary>
public enum PostInstallOutcomeKind
{
    /// <summary>Nothing to do: the install failed, or the app has no command.</summary>
    NotApplicable = 0,

    /// <summary>The gate refused it; nothing ran.</summary>
    Blocked = 1,

    /// <summary>It ran and exited zero.</summary>
    Ran = 2,

    /// <summary>It ran and exited nonzero.</summary>
    Failed = 3,

    /// <summary>It ran too long and its process tree was killed.</summary>
    TimedOut = 4
}

/// <summary>The result of one post-install step. Carries no command text.</summary>
public sealed record PostInstallOutcome(PostInstallOutcomeKind Kind, string? Reason = null, int? ExitCode = null)
{
    /// <summary>True when the app installed but its command did not succeed.</summary>
    public bool IsFailure => Kind is PostInstallOutcomeKind.Failed or PostInstallOutcomeKind.TimedOut;
}

/// <summary>
/// Runs one app's post-install command. This is the only place the engine needs to know about:
/// it decides nothing itself, and the gate is evaluated at call time so an approval or an origin
/// change between profile load and execution is honoured.
/// </summary>
public sealed class PostInstallStep
{
    private readonly ProfileOriginStore _origins;
    private readonly PostInstallApprovalStore _approvals;
    private readonly PostInstallRunner _runner;

    public PostInstallStep(ProfileOriginStore origins, PostInstallApprovalStore approvals, PostInstallRunner runner)
    {
        _origins = origins;
        _approvals = approvals;
        _runner = runner;
    }

    /// <summary>Production wiring: both stores live in the app's state folder.</summary>
    public static PostInstallStep ForState(AppPaths paths, IAppLog log) => new(
        new ProfileOriginStore(Path.Combine(paths.State, "profile-origins.json"), log),
        new PostInstallApprovalStore(Path.Combine(paths.State, "post-install-approvals.json")),
        new PostInstallRunner(log));

    public async Task<PostInstallOutcome> RunAfterInstallAsync(
        AppProfile profile,
        AppEntry app,
        bool installSucceeded,
        CancellationToken cancellationToken = default)
    {
        if (!installSucceeded)
        {
            return new PostInstallOutcome(PostInstallOutcomeKind.NotApplicable, "The install did not succeed.");
        }

        if (app.PostInstall is not { } command)
        {
            return new PostInstallOutcome(PostInstallOutcomeKind.NotApplicable);
        }

        // Evaluated now, not at profile load: the stores are read fresh and the approved hash is
        // compared with the exact command about to run.
        var verdict = new PostInstallCommandGate(_origins, _approvals).EvaluateCommand(profile, app, command);
        if (!verdict.MayRun)
        {
            return new PostInstallOutcome(PostInstallOutcomeKind.Blocked, $"{verdict.Decision}: {verdict.Reason}");
        }

        var result = await _runner.RunAsync(app, command, cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
        {
            return new PostInstallOutcome(PostInstallOutcomeKind.TimedOut, "Timed out.");
        }

        return result.Succeeded
            ? new PostInstallOutcome(PostInstallOutcomeKind.Ran, null, result.ExitCode)
            : new PostInstallOutcome(PostInstallOutcomeKind.Failed, result.Failure, result.ExitCode);
    }
}
