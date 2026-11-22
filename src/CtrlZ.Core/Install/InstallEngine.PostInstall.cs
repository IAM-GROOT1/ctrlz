// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Models;

namespace CtrlZ.Core.Install;

/// <summary>
/// The engine's only post-install entry point. Everything else about a command — whether it is
/// allowed, which origin it has, whether it timed out — belongs to <see cref="PostInstallStep"/>.
/// </summary>
public sealed partial class InstallEngine
{
    /// <summary>
    /// Runs the app's post-install command after a successful install. The step builds its gate at
    /// this moment, so the origin and approval stores are read now rather than at profile load.
    /// </summary>
    private Task<PostInstallOutcome> RunPostInstallAsync(
        AppProfile profile,
        AppEntry app,
        CancellationToken cancellationToken) =>
        PostInstallStep
            .ForState(_paths, _log)
            .RunAfterInstallAsync(profile, app, installSucceeded: true, cancellationToken);
}
