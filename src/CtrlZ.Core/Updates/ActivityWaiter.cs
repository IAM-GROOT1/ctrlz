// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

namespace CtrlZ.Core.Updates;

/// <summary>
/// Waits until no install queue, backup or restore is running. Kept in Core so the
/// deferral rules can be unit-tested without a UI.
/// </summary>
public static class ActivityWaiter
{
    /// <summary>
    /// Returns true once the monitor is idle, or false if <paramref name="timeout"/> elapses
    /// first. Throws <see cref="OperationCanceledException"/> only when the caller cancels.
    /// </summary>
    public static async Task<bool> WaitForIdleAsync(
        IAppActivityMonitor monitor,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        // A monotonic clock: DateTime.UtcNow can be adjusted under a loaded CI runner.
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        while (monitor.IsBusy)
        {
            var remaining = timeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler handler = (_, _) =>
            {
                // Only waking up when the monitor is actually idle removes any doubt about a
                // transition that fires while we are not looking.
                if (!monitor.IsBusy)
                {
                    idle.TrySetResult();
                }
            };
            monitor.BusyChanged += handler;

            try
            {
                // Re-check after subscribing so a transition between the two cannot be missed.
                if (!monitor.IsBusy)
                {
                    return true;
                }

                await idle.Task.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return false;
            }
            finally
            {
                monitor.BusyChanged -= handler;
            }
        }

        return true;
    }
}
