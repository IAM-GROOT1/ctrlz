// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Updates;

namespace CtrlZ.Core.Tests;

public class UpdatePromptPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static UpdatePromptState State(
        bool autoCheck = true,
        string? skipped = null,
        DateTimeOffset? remindAfter = null) =>
        new(autoCheck, skipped, remindAfter);

    [Fact]
    public void Prompts_when_nothing_suppresses_it()
    {
        var decision = UpdatePromptPolicy.Decide(State(), V("1.2.0"), Now, manual: false);

        Assert.Equal(UpdatePromptDecision.Show, decision);
    }

    [Fact]
    public void Automatic_checks_off_suppresses_only_the_automatic_prompt()
    {
        Assert.Equal(
            UpdatePromptDecision.AutomaticChecksOff,
            UpdatePromptPolicy.Decide(State(autoCheck: false), V("1.2.0"), Now, manual: false));

        Assert.Equal(
            UpdatePromptDecision.Show,
            UpdatePromptPolicy.Decide(State(autoCheck: false), V("1.2.0"), Now, manual: true));
    }

    [Fact]
    public void Skipped_version_is_not_offered_again()
    {
        var decision = UpdatePromptPolicy.Decide(State(skipped: "1.2.0"), V("1.2.0"), Now, manual: false);

        Assert.Equal(UpdatePromptDecision.Skipped, decision);
    }

    [Fact]
    public void Skipped_version_is_still_kept_quiet_on_a_manual_check()
    {
        var decision = UpdatePromptPolicy.Decide(State(skipped: "1.2.0"), V("1.2.0"), Now, manual: true);

        Assert.Equal(UpdatePromptDecision.Skipped, decision);
    }

    [Fact]
    public void A_newer_version_overrides_an_older_skip()
    {
        var decision = UpdatePromptPolicy.Decide(State(skipped: "1.2.0"), V("1.3.0"), Now, manual: false);

        Assert.Equal(UpdatePromptDecision.Show, decision);
    }

    [Fact]
    public void Skipping_a_newer_version_also_suppresses_the_older_one()
    {
        var decision = UpdatePromptPolicy.Decide(State(skipped: "1.4.0"), V("1.3.0"), Now, manual: false);

        Assert.Equal(UpdatePromptDecision.Skipped, decision);
    }

    [Fact]
    public void An_unparsable_skipped_version_does_not_suppress_anything()
    {
        var decision = UpdatePromptPolicy.Decide(State(skipped: "not-a-version"), V("1.2.0"), Now, manual: false);

        Assert.Equal(UpdatePromptDecision.Show, decision);
    }

    [Fact]
    public void Remind_me_later_suppresses_until_the_delay_expires()
    {
        var state = State(remindAfter: Now.AddHours(4));

        Assert.Equal(UpdatePromptDecision.RemindPending, UpdatePromptPolicy.Decide(state, V("1.2.0"), Now, manual: false));
        Assert.Equal(UpdatePromptDecision.Show, UpdatePromptPolicy.Decide(state, V("1.2.0"), Now.AddHours(5), manual: false));
    }

    [Fact]
    public void An_expired_reminder_prompts_again()
    {
        var state = State(remindAfter: Now.AddSeconds(-1));

        Assert.Equal(UpdatePromptDecision.Show, UpdatePromptPolicy.Decide(state, V("1.2.0"), Now, manual: false));
    }

    [Fact]
    public void A_manual_check_ignores_a_pending_reminder()
    {
        var state = State(remindAfter: Now.AddHours(4));

        Assert.Equal(UpdatePromptDecision.Show, UpdatePromptPolicy.Decide(state, V("1.2.0"), Now, manual: true));
    }

    [Fact]
    public void Skip_beats_a_pending_reminder()
    {
        var state = State(skipped: "1.2.0", remindAfter: Now.AddHours(4));

        Assert.Equal(UpdatePromptDecision.Skipped, UpdatePromptPolicy.Decide(state, V("1.2.0"), Now, manual: false));
    }

    private static SemanticVersion V(string value) => SemanticVersion.Parse(value);
}

public class ActivityWaiterTests
{
    [Fact]
    public async Task Returns_immediately_when_nothing_is_running()
    {
        var monitor = new AppActivityMonitor();

        var idle = await ActivityWaiter.WaitForIdleAsync(monitor, TimeSpan.FromSeconds(5));

        Assert.True(idle);
    }

    [Fact]
    public async Task Waits_until_the_running_operation_finishes()
    {
        var monitor = new AppActivityMonitor();
        var scope = monitor.Begin("Installing apps");

        var waiting = ActivityWaiter.WaitForIdleAsync(monitor, TimeSpan.FromSeconds(10));
        await Task.Delay(120);
        Assert.False(waiting.IsCompleted);

        scope.Dispose();

        Assert.True(await waiting);
    }

    [Fact]
    public async Task Gives_up_after_the_timeout_instead_of_hanging()
    {
        var monitor = new AppActivityMonitor();
        using var scope = monitor.Begin("Backing up browser profiles");

        var idle = await ActivityWaiter.WaitForIdleAsync(monitor, TimeSpan.FromMilliseconds(150));

        Assert.False(idle);
    }

    [Fact]
    public async Task Waits_for_every_nested_operation_to_finish()
    {
        var monitor = new AppActivityMonitor();
        var installing = monitor.Begin("Installing apps");
        var backingUp = monitor.Begin("Backing up browser profiles");

        var waiting = ActivityWaiter.WaitForIdleAsync(monitor, TimeSpan.FromSeconds(10));
        await Task.Delay(80);
        installing.Dispose();
        await Task.Delay(80);
        Assert.False(waiting.IsCompleted);

        backingUp.Dispose();

        Assert.True(await waiting);
    }

    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        var monitor = new AppActivityMonitor();
        using var scope = monitor.Begin("Installing apps");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ActivityWaiter.WaitForIdleAsync(monitor, TimeSpan.FromSeconds(30), cts.Token));
    }
}
