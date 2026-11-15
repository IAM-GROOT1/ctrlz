namespace CtrlZ.Core.Pro;

/// <summary>
/// Entry point implemented by the optional, separately licensed Pro assembly.
/// The free core never references Pro types directly.
/// </summary>
public interface IProModule
{
    string Id { get; }
    string DisplayName { get; }
    string Description { get; }
    Version Version { get; }
    IReadOnlyList<IProFeature> Features { get; }

    /// <summary>Register any services the Pro features need.</summary>
    void RegisterServices(IProServiceRegistry services);
}

/// <summary>A page or capability contributed by a Pro module.</summary>
public interface IProFeature
{
    string Id { get; }
    string DisplayName { get; }
    string Description { get; }

    /// <summary>Segoe Fluent Icons glyph shown by the core UI.</summary>
    string Glyph { get; }

    /// <summary>
    /// Optional UI contribution. The core passes this object to WPF only when the feature is
    /// licensed; returning null is valid for service-only features.
    /// </summary>
    object? CreatePage();
}

/// <summary>Small service bag owned by the core composition root.</summary>
public interface IProServiceRegistry
{
    void Register(string key, object service);
    object? Resolve(string key);
}
