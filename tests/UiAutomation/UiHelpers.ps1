# SPDX-License-Identifier: AGPL-3.0-only
# Copyright (c) 2026 iam-groot1
#
# Shared UI Automation helpers for driving the real CTRL+Z window from a script.

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms

function Wait-AppWindow {
    param([int]$ProcessId, [int]$TimeoutSec = 60)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
        if ($proc -and $proc.MainWindowHandle -ne 0) {
            return [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
        }
        Start-Sleep -Milliseconds 250
    }
    throw "App window did not appear within $TimeoutSec s"
}

function Get-ProcessWindows {
    param([int]$ProcessId)
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children, $condition)
    $result = @()
    foreach ($window in $windows) {
        $result += [pscustomobject]@{
            Name = $window.Current.Name
            Element = $window
        }
    }
    return $result
}

function Wait-WindowNamed {
    param([int]$ProcessId, [string]$Name, [int]$TimeoutSec = 30)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        foreach ($window in (Get-ProcessWindows -ProcessId $ProcessId)) {
            if ($window.Name -eq $Name) { return $window.Element }
        }
        Start-Sleep -Milliseconds 200
    }
    return $null
}

function Find-ByName {
    param($Root, [string]$Name)
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Wait-ByName {
    param($Root, [string]$Name, [int]$TimeoutSec = 20)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $element = Find-ByName -Root $Root -Name $Name
        if ($element) { return $element }
        Start-Sleep -Milliseconds 200
    }
    return $null
}

function Invoke-Element {
    param($Element)
    if (-not $Element) { throw "Cannot invoke a null element" }
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Get-ElementNames {
    param($Root, [int]$Limit = 200)
    $all = $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    $names = @()
    $count = 0
    foreach ($element in $all) {
        $names += [pscustomobject]@{
            Type = $element.Current.ControlType.ProgrammaticName
            Name = $element.Current.Name
        }
        $count++
        if ($count -ge $Limit) { break }
    }
    return $names
}

function Save-WindowShot {
    param($Element, [string]$Path)
    $rect = $Element.Current.BoundingRectangle
    $width = [int]$rect.Width
    $height = [int]$rect.Height
    if ($width -le 0 -or $height -le 0) { return $false }
    $bitmap = New-Object System.Drawing.Bitmap $width, $height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen([int]$rect.X, [int]$rect.Y, 0, 0, $bitmap.Size)
    $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose()
    $bitmap.Dispose()
    return $true
}

Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class WinEnum {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    public static List<string> Titles(uint target) {
        var list = new List<string>();
        EnumWindows((h, l) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid == target && IsWindowVisible(h)) {
                var t = new StringBuilder(512); GetWindowTextW(h, t, 512);
                if (t.Length > 0) list.Add(t.ToString() + "|" + h.ToInt64());
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
"@

function Get-WindowByTitle {
    param([int]$ProcessId, [string]$Title, [int]$TimeoutSec = 20)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        foreach ($entry in [WinEnum]::Titles([uint32]$ProcessId)) {
            $parts = $entry -split '\|'
            if ($parts[0] -eq $Title) {
                return [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr][int64]$parts[1])
            }
        }
        Start-Sleep -Milliseconds 250
    }
    return $null
}
