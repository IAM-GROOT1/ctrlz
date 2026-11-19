// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Text.Json;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Logging;

namespace CtrlZ.Core.Safety;

/// <summary>
/// Local, per-user state for the readiness checklist: which items the user marked handled
/// or ignored, and which drifted apps they never want to see again. Stored next to the
/// other app state, never uploaded anywhere.
/// </summary>
public sealed class SafetyState
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Check ids the user marked as handled.</summary>
    public List<string> HandledChecks { get; set; } = [];

    /// <summary>Check ids the user chose to ignore.</summary>
    public List<string> IgnoredChecks { get; set; } = [];

    /// <summary>Stable keys of drifted apps the user chose to ignore permanently.</summary>
    public List<string> IgnoredDriftKeys { get; set; } = [];
}

/// <summary>Reads and writes <see cref="SafetyState"/> with the same atomic-write rules as settings.</summary>
public sealed class SafetyStateStore
{
    private readonly string _path;
    private readonly IAppLog _log;
    private readonly object _gate = new();
    private SafetyState? _cached;

    public SafetyStateStore(string path, IAppLog log)
    {
        _path = path;
        _log = log;
    }

    public SafetyState Load()
    {
        lock (_gate)
        {
            if (_cached is not null)
            {
                return _cached;
            }

            try
            {
                if (File.Exists(_path))
                {
                    var json = File.ReadAllText(_path);
                    _cached = JsonSerializer.Deserialize<SafetyState>(json, JsonDefaults.Profile) ?? new SafetyState();
                    return _cached;
                }
            }
            catch (Exception ex)
            {
                _log.Write(
                    LogLevel.Warning,
                    "Could not read safety state; using defaults. " + LogPrivacy.Describe(ex));
            }

            _cached = new SafetyState();
            return _cached;
        }
    }

    public void Save(SafetyState state)
    {
        lock (_gate)
        {
            _cached = state;
            try
            {
                FileUtil.AtomicWriteAllText(_path, JsonSerializer.Serialize(state, JsonDefaults.Profile));
            }
            catch (Exception ex)
            {
                _log.Write(LogLevel.Error, "Could not save safety state. " + LogPrivacy.Describe(ex));
            }
        }
    }

    /// <summary>Compares ids case-insensitively, because they are authored strings.</summary>
    public static bool Contains(IEnumerable<string> ids, string id) =>
        ids.Any(existing => string.Equals(existing, id, StringComparison.OrdinalIgnoreCase));

    public static void Add(List<string> ids, string id)
    {
        if (!Contains(ids, id))
        {
            ids.Add(id);
        }
    }

    public static void Remove(List<string> ids, string id) =>
        ids.RemoveAll(existing => string.Equals(existing, id, StringComparison.OrdinalIgnoreCase));
}
