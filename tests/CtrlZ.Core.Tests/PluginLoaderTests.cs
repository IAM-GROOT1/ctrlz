using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Pro;
using CtrlZ.Pro.TestModule;

namespace CtrlZ.Core.Tests;

public class PluginLoaderTests
{
    [Fact]
    public void Missing_pro_module_returns_no_module_without_error()
    {
        using var temp = new TestDirectory();
        var result = ProModuleLoader.LoadFromDirectory(temp.Root, TestFactory.Log);

        Assert.False(result.IsLoaded);
        Assert.False(result.IsInstalled);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void Corrupt_pro_assembly_is_reported_not_thrown()
    {
        using var temp = new TestDirectory();
        temp.Write("CtrlZ.Pro.dll", "this is not a managed assembly");

        var result = ProModuleLoader.LoadFromDirectory(temp.Root, TestFactory.Log);

        Assert.False(result.IsLoaded);
        Assert.True(result.IsInstalled);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public void Valid_pro_module_loads_features_and_registers_services()
    {
        using var temp = new TestDirectory();
        var source = typeof(TestProModule).Assembly.Location;
        File.Copy(source, temp.PathOf("CtrlZ.Pro.dll"), overwrite: true);

        var result = ProModuleLoader.LoadFromDirectory(temp.Root, TestFactory.Log);

        Assert.True(result.IsLoaded);
        Assert.Equal("test.pro", result.Module!.Id);
        Assert.Single(result.Module.Features);
        Assert.Equal("test.pro.feature", result.Module.Features[0].Id);

        var services = new ProServiceRegistry(TestFactory.Log);
        result.Module.RegisterServices(services);
        Assert.Equal("registered", services.Resolve("test.pro.marker"));
    }

    [Fact]
    public void App_with_no_pro_module_builds_a_free_core()
    {
        using var temp = new TestDirectory();
        var appDir = temp.CreateDirectory("app");
        var paths = new AppPaths(temp.PathOf("data"));
        paths.EnsureCreated();

        var result = ProModuleLoader.Load(paths, TestFactory.Log, appDir);

        Assert.False(result.IsLoaded);
        Assert.False(result.IsInstalled);
    }
}
