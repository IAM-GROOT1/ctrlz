// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Updates;

namespace CtrlZ.Core.Tests;

/// <summary>
/// The installed-copy update path with a stand-in installer (a .cmd file) and stand-in builds:
/// small Windows tools that exit at once, which is exactly what a new build that crashes on start
/// looks like to the helper.
/// </summary>
public sealed class InstalledUpdateTests : IDisposable
{
    private static readonly string OldBuild = Path.Combine(Environment.SystemDirectory, "where.exe");
    private static readonly string NewBuild = Path.Combine(Environment.SystemDirectory, "hostname.exe");

    private readonly TestDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string App => _temp.PathOf("app", "CTRL+Z.exe");

    private string Cli => _temp.PathOf("app", "ctrlz.exe");

    private string StatePath => _temp.PathOf("state", UpdateProbation.FileName);

    private UpdateApplier Applier() => new(TestFactory.Log, new UpdateProbation(StatePath, TestFactory.Log));

    /// <summary>An installed copy (app and command line) and a Setup that replaces both, then exits with <paramref name="exitCode"/>.</summary>
    private UpdateApplyRequest Installed(int exitCode)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(App)!);
        File.Copy(OldBuild, App, overwrite: true);
        File.Copy(OldBuild, Cli, overwrite: true);
        var setup = _temp.PathOf("download", "setup.cmd");
        Directory.CreateDirectory(Path.GetDirectoryName(setup)!);
        File.WriteAllText(setup,
            $"@echo off\r\ncopy /y \"{NewBuild}\" \"{App}\" >nul\r\ncopy /y \"{NewBuild}\" \"{Cli}\" >nul\r\nexit /b {exitCode}\r\n");
        return new UpdateApplyRequest { Kind = nameof(UpdateInstallKind.Installed), DownloadedFile = setup, TargetExe = App, Version = "9.9.9" };
    }

    private static bool SameBytes(string a, string b) => File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));

    [Fact]
    public async Task A_new_build_that_closes_before_its_window_is_rolled_back_with_its_command_line()
    {
        var result = await Applier().ApplyAsync(Installed(exitCode: 0));

        Assert.False(result.Success);
        Assert.Contains("rolled back", result.Message);
        Assert.True(result.RolledBack, "the app skips this version afterwards");
        Assert.Equal("9.9.9", result.Version);
        Assert.True(SameBytes(App, OldBuild), "the previous CTRL+Z.exe is back");
        Assert.True(SameBytes(Cli, OldBuild), "the previous ctrlz.exe is back");
        Assert.False(File.Exists(App + ".old"));
        Assert.False(File.Exists(Cli + ".old"));
        Assert.False(File.Exists(StatePath), "no probation is left behind after a rollback");
    }

    [Fact]
    public async Task A_failed_installer_leaves_the_install_alone_and_drops_the_rollback_copy()
    {
        var result = await Applier().ApplyAsync(Installed(exitCode: 3));

        Assert.False(result.Success);
        Assert.Contains("code 3", result.Message);
        Assert.False(File.Exists(App + ".old"));
        Assert.False(File.Exists(Cli + ".old"));
        Assert.False(File.Exists(StatePath));
    }
}
