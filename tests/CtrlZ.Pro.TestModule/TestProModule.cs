using CtrlZ.Core.Pro;

namespace CtrlZ.Pro.TestModule;

/// <summary>Test-only dummy module. It is never referenced by the shipped app.</summary>
public sealed class TestProModule : IProModule
{
    public string Id => "test.pro";
    public string DisplayName => "Test Pro Module";
    public string Description => "Synthetic module used to verify the plugin boundary.";
    public Version Version => new(1, 0, 0);

    public IReadOnlyList<IProFeature> Features { get; } =
    [
        new TestProFeature()
    ];

    public void RegisterServices(IProServiceRegistry services) =>
        services.Register("test.pro.marker", "registered");
}

public sealed class TestProFeature : IProFeature
{
    public string Id => "test.pro.feature";
    public string DisplayName => "Test Pro Feature";
    public string Description => "A synthetic feature.";
    public string Glyph => "\uE8A5";
    public object? CreatePage() => null;
}
