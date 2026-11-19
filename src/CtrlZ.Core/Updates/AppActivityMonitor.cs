// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

namespace CtrlZ.Core.Updates;

/// <summary>
/// Tracks work that must never be interrupted by an update (install queue, backup, restore).
/// The updater consults this before prompting and again before applying.
/// </summary>
public interface IAppActivityMonitor
{
    bool IsBusy { get; }

    /// <summary>Label of the most recently started activity, for log messages.</summary>
    string? CurrentActivity { get; }

    event EventHandler? BusyChanged;

    /// <summary>Marks an activity as running until the returned scope is disposed.</summary>
    IDisposable Begin(string activity);
}

/// <summary>Thread-safe, re-entrant activity counter. Disposing a scope is idempotent.</summary>
public sealed class AppActivityMonitor : IAppActivityMonitor
{
    private readonly object _gate = new();
    private readonly Dictionary<long, string> _activities = new();
    private long _nextId;

    public bool IsBusy
    {
        get
        {
            lock (_gate)
            {
                return _activities.Count > 0;
            }
        }
    }

    public string? CurrentActivity
    {
        get
        {
            lock (_gate)
            {
                return _activities.Count == 0 ? null : _activities.Values.Last();
            }
        }
    }

    public event EventHandler? BusyChanged;

    public IDisposable Begin(string activity)
    {
        long id;
        string name = string.IsNullOrWhiteSpace(activity) ? "Working" : activity;

        lock (_gate)
        {
            id = ++_nextId;
            _activities[id] = name;
        }

        RaiseBusyChanged();
        return new Scope(this, id);
    }

    private void End(long id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _activities.Remove(id);
        }

        if (removed)
        {
            RaiseBusyChanged();
        }
    }

    private void RaiseBusyChanged() => BusyChanged?.Invoke(this, EventArgs.Empty);

    private sealed class Scope : IDisposable
    {
        private readonly AppActivityMonitor _owner;
        private readonly long _id;
        private int _disposed;

        public Scope(AppActivityMonitor owner, long id)
        {
            _owner = owner;
            _id = id;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.End(_id);
            }
        }
    }
}
