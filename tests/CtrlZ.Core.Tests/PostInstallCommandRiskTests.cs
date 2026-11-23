// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Install;
using CtrlZ.Core.Models;

namespace CtrlZ.Core.Tests;

public class PostInstallCommandRiskTests
{
    private static PostInstallCommand Command(string exe, params string[] args) =>
        new() { Executable = exe, Arguments = args.ToList(), Enabled = true };

    [Theory]
    [InlineData("reg", "add", @"HKCU\Software\X", "/f")]
    [InlineData("reg.exe", "add", @"HKCU\Software\X", "/f")]
    [InlineData(@"C:\Windows\System32\reg.exe", "add", @"HKCU\Software\X", "/f")]
    [InlineData(@"C:\Windows\System32\REG.EXE", "delete", @"HKCU\Software\X", "/f")]
    [InlineData(@"C:\Windows\System32\sc.exe", "create", "svc", "binPath=", @"C:\x.exe")]
    [InlineData("sc", "config", "svc", "start=", "auto")]
    [InlineData(@"C:\Windows\System32\cmd.exe", "/c", @"reg add HKCU\Software\X /f")]
    [InlineData(@"C:\Windows\System32\schtasks.exe", "/create", "/tn", "x")]
    public void Registry_service_and_task_changes_are_flagged_however_the_program_is_written(string exe, params string[] args)
    {
        Assert.Contains("changes the registry, services or scheduled tasks", PostInstallCommandRisk.Scan(Command(exe, args)));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\reg.exe", "query", @"HKCU\Software\X")]
    [InlineData(@"C:\Windows\System32\reg.exe", "import", "settings.reg")] // importing an app's settings is the usual safe example
    [InlineData(@"C:\Windows\System32\sc.exe", "query", "svc")]
    [InlineData(@"C:\Windows\System32\whoami.exe", "/user")]
    [InlineData(@"C:\Program Files\Regex Tool\regextool.exe", "add")]
    public void Read_only_and_unrelated_commands_are_not_flagged(string exe, params string[] args)
    {
        Assert.Empty(PostInstallCommandRisk.Scan(Command(exe, args)));
    }
}
