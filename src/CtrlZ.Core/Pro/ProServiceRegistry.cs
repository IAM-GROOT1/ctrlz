using CtrlZ.Core.Logging;

namespace CtrlZ.Core.Pro;

public sealed class ProServiceRegistry : IProServiceRegistry
{
    private readonly Dictionary<string, object> _services = new(StringComparer.OrdinalIgnoreCase);
    private readonly IAppLog? _log;

    public ProServiceRegistry(IAppLog? log = null)
    {
        _log = log;
    }

    public void Register(string key, object service)
    {
        if (string.IsNullOrWhiteSpace(key) || service is null)
        {
            throw new ArgumentException("A Pro service needs a key and an instance.");
        }

        _services[key] = service;
        _log?.Write(LogLevel.Info, $"Pro service registered: {key}.");
    }

    public object? Resolve(string key) =>
        !string.IsNullOrWhiteSpace(key) && _services.TryGetValue(key, out var service) ? service : null;

    public IReadOnlyDictionary<string, object> Services => _services;
}
