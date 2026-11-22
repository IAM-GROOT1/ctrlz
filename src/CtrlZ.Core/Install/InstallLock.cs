// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

namespace CtrlZ.Core.Install;

/// <summary>
/// Cross-process lock held while an install run is active, shared by the app window and the
/// command line, so two runs never install at the same time on this PC. A named mutex is released
/// by the OS if the holder crashes. The mutex lives on a dedicated thread because a mutex must be
/// released by the thread that took it, and the install run is async.
/// </summary>
public sealed class InstallLock : IDisposable
{
    public const string DefaultName = @"Local\CtrlZ.InstallRun";

    private readonly ManualResetEventSlim _release = new(false);
    private readonly Thread _owner;
    private int _disposed;

    private InstallLock(Thread owner)
    {
        _owner = owner;
    }

    /// <summary>Returns the held lock, or null when another run holds it.</summary>
    public static InstallLock? TryAcquire(string name = DefaultName)
    {
        var acquired = new ManualResetEventSlim(false);
        var success = false;
        InstallLock? holder = null;

        var thread = new Thread(() =>
        {
            using var mutex = new Mutex(false, name);
            bool owned;
            try
            {
                owned = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // The previous holder crashed; the lock is ours now.
                owned = true;
            }

            success = owned;
            acquired.Set();
            if (!owned)
            {
                return;
            }

            holder!._release.Wait();
            mutex.ReleaseMutex();
        })
        {
            IsBackground = true,
            Name = "CTRL+Z install lock"
        };

        holder = new InstallLock(thread);
        thread.Start();
        acquired.Wait();
        acquired.Dispose();

        if (success)
        {
            return holder;
        }

        thread.Join();
        holder._release.Dispose();
        return null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _release.Set();
        _owner.Join();
        _release.Dispose();
    }
}
