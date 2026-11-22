// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.IO;
using CtrlZ.Core.Install;
using CtrlZ.Core.Models;

namespace CtrlZ.App.Services;

/// <summary>
/// The decision every install run passes before the engine starts: resolve the exact profile the
/// engine will run, then require the command confirmations it needs. Kept free of WPF so the
/// production composition is the one the tests exercise.
/// </summary>
public static class InstallStartGuard
{
    /// <summary>True when the run may start. False means nothing may run.</summary>
    public static bool MayStart(
        InstallState state,
        Func<string, AppProfile> loadProfile,
        Func<AppProfile, bool> confirmCommands)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(loadProfile);
        ArgumentNullException.ThrowIfNull(confirmCommands);

        if (state.ProfileSnapshot is null && !string.IsNullOrWhiteSpace(state.ProfilePath) && File.Exists(state.ProfilePath))
        {
            state.ProfileSnapshot = loadProfile(state.ProfilePath);
        }

        return state.ProfileSnapshot is not { } profile || confirmCommands(profile);
    }

    /// <summary>
    /// The production confirmation: reads origin and approvals fresh from the state folder, then asks
    /// the first-run and risky confirmations through <paramref name="confirm"/> (title, message, danger).
    /// </summary>
    public static Func<AppProfile, bool> CommandConsent(
        string stateFolder,
        Func<AppProfile, ProfileOrigin> originOf,
        Func<string, string, bool, bool> confirm) => profile =>
    {
        var approvals = new PostInstallApprovalStore(Path.Combine(stateFolder, PostInstallStep.ApprovalsFileName));
        var firstRun = new PostInstallFirstRunStore(Path.Combine(stateFolder, PostInstallFirstRunStore.FileName));
        var lines = CommandPreview.ForProfile(profile, originOf(profile), approvals);
        return PostInstallConsent.Ask(lines, profile.Id, firstRun, confirm);
    };
}
