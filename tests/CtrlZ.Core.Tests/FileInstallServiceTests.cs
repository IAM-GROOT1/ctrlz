// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.IO.Compression;
using CtrlZ.Core.Install;
using CtrlZ.Core.Infrastructure;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Tests;

public class FileInstallServiceTests
{
    private static (FileInstallService Service, AppPaths Paths) CreateService(TestDirectory temp)
    {
        var paths = new AppPaths(temp.PathOf("data"));
        paths.EnsureCreated();
        return (new FileInstallService(paths, new ProcessRunner(), TestFactory.Log), paths);
    }

    [Fact]
    public async Task Runs_an_executable_with_custom_switches_and_reports_success()
    {
        using var temp = new TestDirectory();
        var (service, _) = CreateService(temp);
        var app = new AppEntry
        {
            Name = "Fake",
            Source = AppSource.LocalFile,
            SilentArgs = "/c exit 0"
        };
        var context = new InstallContext(new AppPaths(temp.PathOf("data")), new FakeInstallLogSink(),
            TestFactory.Log, false);

        var result = await service.RunInstallerAsync(app, Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(InstallStatus.Success, result.Status);
    }

    [Fact]
    public async Task Nonzero_exit_is_a_failure_with_exit_code()
    {
        using var temp = new TestDirectory();
        var (service, _) = CreateService(temp);
        var paths = new AppPaths(temp.PathOf("data"));
        var app = new AppEntry
        {
            Name = "Fake",
            Source = AppSource.LocalFile,
            SilentArgs = "/c exit 5"
        };
        var context = new InstallContext(paths, new FakeInstallLogSink(), TestFactory.Log, false);

        var result = await service.RunInstallerAsync(app, Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(InstallStatus.Failed, result.Status);
        Assert.Equal(5, result.ExitCode);
    }

    [Fact]
    public async Task Portable_zip_is_extracted_to_the_apps_folder()
    {
        using var temp = new TestDirectory();
        var (service, paths) = CreateService(temp);
        var archive = temp.PathOf("portable.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("tool.exe");
            await using var stream = entry.Open();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync("fake");
        }

        var app = new AppEntry { Name = "Portable Tool", Source = AppSource.GitHub };
        var context = new InstallContext(paths, new FakeInstallLogSink(), TestFactory.Log, false);
        var result = await service.InstallAsync(app, archive, GitHubAssetKind.PortableArchive, context,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(paths.Apps, "portable-tool", "tool.exe")));
    }
}
