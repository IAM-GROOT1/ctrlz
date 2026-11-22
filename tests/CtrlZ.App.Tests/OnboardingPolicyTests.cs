// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.App.ViewModels;
using CtrlZ.Core.Models;
using Xunit;

namespace CtrlZ.App.Tests;

public class OnboardingPolicyTests
{
    [Fact]
    public void A_new_user_sees_the_guide() =>
        Assert.Equal(OnboardingDecision.Show, OnboardingPolicy.Decide(new AppSettings(), profileCount: 1, appCount: 0, autoInstall: false));

    [Fact]
    public void Someone_who_saw_it_never_sees_it_again() =>
        Assert.Equal(OnboardingDecision.None,
            OnboardingPolicy.Decide(new AppSettings { HasCompletedOnboarding = true }, 1, 0, false));

    [Theory]
    [InlineData(1, 12)]
    [InlineData(2, 0)]
    public void An_existing_user_upgrading_is_not_interrupted(int profiles, int apps) =>
        Assert.Equal(OnboardingDecision.MarkSeen, OnboardingPolicy.Decide(new AppSettings(), profiles, apps, false));

    [Fact]
    public void A_resumed_install_is_never_interrupted() =>
        Assert.Equal(OnboardingDecision.None, OnboardingPolicy.Decide(new AppSettings(), 1, 0, autoInstall: true));
}
