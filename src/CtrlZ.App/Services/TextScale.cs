// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Windows;
using System.Windows.Media;
using System.Windows.Shell;
using Microsoft.Win32;

namespace CtrlZ.App.Services;

/// <summary>
/// Follows Windows' Settings → Accessibility → Text size. WPF does not apply that setting by
/// itself, so every window scales its content by the same factor, like zooming the page, and grows
/// its size to match without leaving the screen. The factor is read when a window opens; a change
/// in Windows applies to windows opened afterwards and to the main window at the next start.
/// </summary>
public static class TextScale
{
    private const double Largest = 2.25;

    /// <summary>The Windows text size as a factor: 1.0 at 100 %, up to 2.25 at 225 %.</summary>
    public static double Factor => FromSetting(ReadSetting());

    /// <summary>Turns the registry value (a percentage) into a factor; anything unusable is 1.0.</summary>
    public static double FromSetting(object? value) =>
        value is int percent && percent > 100 ? Math.Min(percent / 100.0, Largest) : 1.0;

    /// <summary>Called by every window right after <c>InitializeComponent</c>.</summary>
    public static void Apply(Window window) => Apply(window, Factor, SystemParameters.WorkArea.Size);

    public static void Apply(Window window, double factor, Size workArea)
    {
        if (factor <= 1.0 || window.Content is not FrameworkElement root)
        {
            return;
        }

        root.LayoutTransform = new ScaleTransform(factor, factor);

        window.MinWidth = Math.Min(window.MinWidth * factor, workArea.Width);
        window.MinHeight = Math.Min(window.MinHeight * factor, workArea.Height);
        if (!double.IsNaN(window.Width))
        {
            window.Width = Math.Min(window.Width * factor, workArea.Width);
        }

        if (!double.IsNaN(window.Height) && window.SizeToContent is not (SizeToContent.Height or SizeToContent.WidthAndHeight))
        {
            window.Height = Math.Min(window.Height * factor, workArea.Height);
        }

        if (window.SizeToContent is SizeToContent.Height or SizeToContent.WidthAndHeight)
        {
            window.MaxHeight = workArea.Height;
        }

        // The draggable title area grows with the title bar it covers.
        if (WindowChrome.GetWindowChrome(window) is { } chrome)
        {
            chrome.CaptionHeight *= factor;
        }

        // A larger window opened at its usual place can reach past the screen's edge.
        window.Loaded += (_, _) => KeepOnScreen(window, SystemParameters.WorkArea);
    }

    /// <summary>Moves a window that reaches past the work area back inside it.</summary>
    public static void KeepOnScreen(Window window, Rect workArea)
    {
        if (window.WindowState != WindowState.Normal)
        {
            return;
        }

        var width = window.ActualWidth;
        var height = window.ActualHeight;
        if (window.Left + width > workArea.Right)
        {
            window.Left = Math.Max(workArea.Left, workArea.Right - width);
        }

        if (window.Top + height > workArea.Bottom)
        {
            window.Top = Math.Max(workArea.Top, workArea.Bottom - height);
        }

        if (window.Left < workArea.Left)
        {
            window.Left = workArea.Left;
        }

        if (window.Top < workArea.Top)
        {
            window.Top = workArea.Top;
        }
    }

    private static object? ReadSetting()
    {
        try
        {
            return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Accessibility", "TextScaleFactor", null);
        }
        catch
        {
            return null;
        }
    }
}
