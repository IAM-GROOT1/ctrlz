using CtrlZ.Core.Install;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Tests;

internal sealed class TestDirectory : IDisposable
{
    public TestDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "CtrlZTests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string PathOf(params string[] parts) => Path.Combine(new[] { Root }.Concat(parts).ToArray());

    public string Write(string relativePath, string content)
    {
        var path = PathOf(relativePath.Split('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public string CreateDirectory(string relativePath)
    {
        var path = PathOf(relativePath.Split('/', '\\'));
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }

                return;
            }
            catch
            {
                Thread.Sleep(100);
            }
        }
    }
}

internal sealed class FakeInstallLogSink : IInstallLogSink
{
    public List<string> Lines { get; } = new();
    public List<InstallProgress> ProgressEvents { get; } = new();
    public void Line(string message) => Lines.Add(message);
    public void Progress(InstallProgress progress) => ProgressEvents.Add(progress);
}

internal sealed class FakeInstaller : CtrlZ.Core.Install.ISourceInstaller
{
    private readonly Func<AppEntry, int> _attemptsBeforeSuccess;
    private readonly Dictionary<string, int> _attempts = new(StringComparer.OrdinalIgnoreCase);
    public FakeInstaller(AppSource source, Func<AppEntry, int>? attemptsBeforeSuccess = null)
    {
        Source = source;
        _attemptsBeforeSuccess = attemptsBeforeSuccess ?? (_ => 0);
    }

    public AppSource Source { get; }
    public List<string> Installed { get; } = new();

    public bool CanHandle(AppSource source) => source == Source;

    public Task<InstallResult> InstallAsync(AppEntry app, InstallContext context, CancellationToken cancellationToken)
    {
        _attempts.TryGetValue(app.Name, out var count);
        _attempts[app.Name] = count + 1;
        if (count < _attemptsBeforeSuccess(app))
        {
            return Task.FromResult(InstallResult.Fail("synthetic failure"));
        }

        Installed.Add(app.Name);
        return Task.FromResult(InstallResult.Ok("synthetic success"));
    }
}

internal static class TestFactory
{
    public static readonly IAppLog Log = NullAppLog.Instance;
}
