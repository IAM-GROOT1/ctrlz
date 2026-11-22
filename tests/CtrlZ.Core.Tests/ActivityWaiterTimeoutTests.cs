// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Updates;

namespace CtrlZ.Core.Tests;

public class ActivityWaiterTimeoutTests
{
    /// <summary>Busy for the first reads, then idle, without ever raising BusyChanged.</summary>
    private sealed class SilentMonitor(int busyReads) : IAppActivityMonitor
    {
        private int _reads;

        public bool IsBusy => Interlocked.Increment(ref _reads) <= busyReads;
        public string? CurrentActivity => null;
        public event EventHandler? BusyChanged { add { } remove { } }
        public IDisposable Begin(string activity) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_timeout_observed_after_the_monitor_went_idle_reports_idle()
    {
        // Two busy reads: the loop check and the re-check after subscribing. The idle transition is
        // never signalled, so the wait times out; the result must reflect the actual idle state.
        var idle = await ActivityWaiter.WaitForIdleAsync(new SilentMonitor(busyReads: 2), TimeSpan.FromMilliseconds(20));

        Assert.True(idle);
    }

    [Fact]
    public async Task A_timeout_while_still_busy_reports_busy()
    {
        var idle = await ActivityWaiter.WaitForIdleAsync(new SilentMonitor(busyReads: int.MaxValue), TimeSpan.FromMilliseconds(20));

        Assert.False(idle);
    }
}
