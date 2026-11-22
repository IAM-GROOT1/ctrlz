// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Models;

namespace CtrlZ.App.ViewModels;

public enum OnboardingDecision
{
    /// <summary>Nothing to do: the guide was already seen.</summary>
    None,

    /// <summary>A new user: show the guide once.</summary>
    Show,

    /// <summary>Someone who already uses CTRL+Z (it has apps) and never saw the guide: record it as seen without showing it.</summary>
    MarkSeen
}

/// <summary>Decides whether the first-run guide appears at startup.</summary>
public static class OnboardingPolicy
{
    /// <param name="autoInstall">An elevated relaunch that resumes an install: never interrupt it.</param>
    public static OnboardingDecision Decide(AppSettings settings, int profileCount, int appCount, bool autoInstall)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.HasCompletedOnboarding || autoInstall)
        {
            return OnboardingDecision.None;
        }

        // Upgraded from an older version with a list already built: the guide would only get in the way.
        return appCount > 0 || profileCount > 1 ? OnboardingDecision.MarkSeen : OnboardingDecision.Show;
    }
}
