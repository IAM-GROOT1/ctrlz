using System.Text.Json;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Logging;

namespace CtrlZ.Core.Licensing;

public sealed class StoredLicense
{
    public string Token { get; set; } = string.Empty;
    public DateTimeOffset? LastSuccessfulCheckUtc { get; set; }
    public DateTimeOffset? LastAttemptUtc { get; set; }
    public bool LastAttemptSucceeded { get; set; }
    public string? LastError { get; set; }
    public string? ServerUrl { get; set; }
}

public sealed class LicenseStore
{
    private readonly string _path;
    private readonly IAppLog _log;
    private readonly object _gate = new();

    public LicenseStore(string path, IAppLog log)
    {
        _path = path;
        _log = log;
    }

    public StoredLicense? Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return null;
                }

                return JsonSerializer.Deserialize<StoredLicense>(
                    File.ReadAllText(_path), JsonDefaults.Compact);
            }
            catch (Exception ex)
            {
                _log.Write(LogLevel.Warning, "Could not read the stored license.", ex);
                return null;
            }
        }
    }

    public void Save(StoredLicense license)
    {
        lock (_gate)
        {
            try
            {
                FileUtil.AtomicWriteAllText(_path, JsonSerializer.Serialize(license, JsonDefaults.Profile));
            }
            catch (Exception ex)
            {
                _log.Write(LogLevel.Error, "Could not save the license.", ex);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
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
                _log.Write(LogLevel.Warning, "Could not remove the stored license.", ex);
            }
        }
    }
}
