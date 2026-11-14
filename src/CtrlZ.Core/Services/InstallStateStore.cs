using System.Text.Json;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Services;

/// <summary>
/// Persists a resumable install session. The profile is snapshotted so an unfinished
/// install can still be resumed after a reboot, even if the profile file moved.
/// </summary>
public sealed class InstallStateStore
{
    private readonly string _path;
    private readonly IAppLog _log;

    public InstallStateStore(string path, IAppLog log)
    {
        _path = path;
        _log = log;
    }

    public string StatePath => _path;

    public InstallState? Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var json = File.ReadAllText(_path);
            var state = JsonSerializer.Deserialize<InstallState>(json, JsonDefaults.Profile);
            if (state is null)
            {
                return null;
            }

            if (state.SchemaVersion > InstallState.CurrentSchemaVersion)
            {
                _log.Write(LogLevel.Warning, "Install state was written by a newer CTRL+Z version; ignoring it.");
                return null;
            }

            state.Items ??= new List<InstallItemState>();
            return state;
        }
        catch (Exception ex)
        {
            _log.Write(LogLevel.Warning, "Could not read the saved install state.", ex);
            return null;
        }
    }

    public void Save(InstallState state)
    {
        try
        {
            state.UpdatedUtc = DateTimeOffset.UtcNow;
            FileUtil.AtomicWriteAllText(_path, JsonSerializer.Serialize(state, JsonDefaults.Profile));
        }
        catch (Exception ex)
        {
            _log.Write(LogLevel.Error, "Could not save install state.", ex);
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (Exception ex)
        {
            _log.Write(LogLevel.Warning, "Could not clear install state.", ex);
        }
    }
}
