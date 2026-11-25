// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Runtime.InteropServices;

namespace CtrlZ.Core.Tweaks;

/// <summary>
/// Tells open windows that a Windows setting changed, as Windows Settings does after you change
/// it there. Without this, a registry change such as dark mode is only picked up by the taskbar
/// and open apps after you sign out.
/// </summary>
public static class WindowsSettingChange
{
    /// <summary>The area name Windows Settings uses for light and dark mode.</summary>
    public const string ColorSet = "ImmersiveColorSet";

    private const int Broadcast = 0xFFFF;
    private const uint SettingChangeMessage = 0x001A;
    private const uint AbortIfHung = 0x0002;
    private const uint TimeoutMs = 1000;

    /// <summary>
    /// Sends the notice to every top-level window. It runs off the caller's thread: a window
    /// that does not answer only delays the notice, never the app.
    /// </summary>
    public static void Announce(string area) =>
        _ = Task.Run(() => SendMessageTimeout(
            Broadcast, SettingChangeMessage, IntPtr.Zero, area, AbortIfHung, TimeoutMs, out _));

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr window, uint message, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
}
