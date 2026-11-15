using System.Reflection;
using System.Runtime.Loader;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Logging;

namespace CtrlZ.Core.Pro;

public sealed class ProModuleLoadResult
{
    public IProModule? Module { get; init; }
    public string? AssemblyPath { get; init; }
    public string? ErrorMessage { get; init; }
    public bool IsLoaded => Module is not null;
    public bool IsInstalled => AssemblyPath is not null;
}

/// <summary>
/// Discovers and loads the optional Pro assembly in an isolated load context. Load failures
/// are normal for the free build: they are logged and the caller continues.
/// </summary>
public static class ProModuleLoader
{
    public const string AssemblyFileName = "CtrlZ.Pro.dll";

    public static ProModuleLoadResult Load(AppPaths paths, IAppLog log, string? applicationDirectory = null)
    {
        var appDirectory = applicationDirectory ?? AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(appDirectory, AssemblyFileName),
            Path.Combine(appDirectory, "Pro", AssemblyFileName),
            Path.Combine(paths.ProModules, AssemblyFileName)
        };

        var existing = candidates.FirstOrDefault(File.Exists);
        if (existing is null)
        {
            log.Write(LogLevel.Info, "No Pro module found. Core features remain fully available.");
            return new ProModuleLoadResult();
        }

        return LoadFile(existing, log);
    }

    public static ProModuleLoadResult LoadFromDirectory(string directory, IAppLog log)
    {
        var path = Path.Combine(directory, AssemblyFileName);
        if (!File.Exists(path))
        {
            log.Write(LogLevel.Info, $"No Pro module at {path}.");
            return new ProModuleLoadResult();
        }

        return LoadFile(path, log);
    }

    private static ProModuleLoadResult LoadFile(string path, IAppLog log)
    {
        PluginLoadContext? context = null;
        try
        {
            context = new PluginLoadContext(path);
            var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(path));
            var moduleType = assembly.GetTypes()
                .Where(type => typeof(IProModule).IsAssignableFrom(type) &&
                               type is { IsAbstract: false, IsInterface: false } &&
                               type.GetConstructor(Type.EmptyTypes) is not null)
                .FirstOrDefault();

            if (moduleType is null)
            {
                context.Unload();
                var message = $"{Path.GetFileName(path)} does not contain a public parameterless IProModule.";
                log.Write(LogLevel.Error, message);
                return new ProModuleLoadResult { AssemblyPath = path, ErrorMessage = message };
            }

            var module = (IProModule)Activator.CreateInstance(moduleType)!;
            log.Write(LogLevel.Info,
                $"Loaded Pro module '{module.DisplayName}' {module.Version} from {path}.");
            return new ProModuleLoadResult { Module = module, AssemblyPath = path };
        }
        catch (Exception ex)
        {
            try { context?.Unload(); } catch { /* best effort */ }
            var message = $"The optional Pro module could not be loaded from {Path.GetFileName(path)}: {ex.Message}";
            log.Write(LogLevel.Error, message, ex);
            return new ProModuleLoadResult { AssemblyPath = path, ErrorMessage = message };
        }
    }

    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public PluginLoadContext(string pluginPath)
            : base("CtrlZ.Pro", isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(pluginPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Core and framework assemblies must keep one type identity, so let the default
            // context resolve those. Only plugin-private dependencies are resolved here.
            if (assemblyName.Name is "CtrlZ.Core" or "System" or "System.Runtime" ||
                assemblyName.Name?.StartsWith("System.", StringComparison.Ordinal) == true ||
                assemblyName.Name?.StartsWith("Microsoft.", StringComparison.Ordinal) == true)
            {
                return null;
            }

            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }
}
