// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Text.Json;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Services;

public sealed class SettingsStore
{
    private readonly string _path;
    private readonly IAppLog _log;
    private readonly object _gate = new();
    private AppSettings? _cached;

    public SettingsStore(string path, IAppLog log)
    {
        _path = path;
        _log = log;
    }

    public AppSettings Load()
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
                    _cached = JsonSerializer.Deserialize<AppSettings>(json, JsonDefaults.Profile) ?? new AppSettings();
                    return _cached;
                }
            }
            catch (Exception ex)
            {
                _log.Write(LogLevel.Warning, "Could not read settings; using defaults.", ex);
            }

            _cached = new AppSettings();
            return _cached;
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            _cached = settings;
            try
            {
                FileUtil.AtomicWriteAllText(_path, JsonSerializer.Serialize(settings, JsonDefaults.Profile));
            }
            catch (Exception ex)
            {
                _log.Write(LogLevel.Error, "Could not save settings.", ex);
            }
        }
    }
}
