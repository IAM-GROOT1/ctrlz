// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Windows;
using System.Windows.Media;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Models;
using Microsoft.Win32;

namespace CtrlZ.App.Services;

public sealed class ThemeService : IDisposable
{
    private readonly Application _application;
    private readonly IAppLog _log;
    private ResourceDictionary? _colorDictionary;
    private ThemeMode _mode = ThemeMode.System;
    private bool _disposed;

    public ThemeService(Application application, IAppLog log)
    {
        _application = application;
        _log = log;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public bool IsDark { get; private set; }

    /// <summary>True while a Windows contrast theme is on; the app then uses its colours.</summary>
    public bool IsContrast { get; private set; }

    public ThemeMode Mode => _mode;

    public event EventHandler? ThemeChanged;

    public void Apply(ThemeMode mode)
    {
        _mode = mode;
        var dark = mode switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => IsSystemDark()
        };

        Apply(dark);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // A contrast theme turned on or off counts whatever the chosen mode is; the light/dark
        // setting of Windows only matters in System mode.
        if (_disposed || (_mode != ThemeMode.System && SystemParameters.HighContrast == IsContrast))
        {
            return;
        }

        try
        {
            _application.Dispatcher.BeginInvoke(() => Apply(_mode));
        }
        catch (Exception ex)
        {
            _log.Write(LogLevel.Debug, "Could not apply a Windows theme change.", ex);
        }
    }

    public void Apply(bool dark)
    {
        var contrast = SystemParameters.HighContrast;
        var replacement = contrast
            ? BuildContrastColors()
            : new ResourceDictionary
            {
                Source = new Uri(dark ? "Themes/Colors.Dark.xaml" : "Themes/Colors.Light.xaml", UriKind.Relative)
            };

        var dictionaries = _application.Resources.MergedDictionaries;
        var existing = _colorDictionary ?? dictionaries.FirstOrDefault(d =>
            d.Source?.OriginalString.Contains("Colors.", StringComparison.OrdinalIgnoreCase) == true);
        if (existing is not null)
        {
            var index = dictionaries.IndexOf(existing);
            dictionaries.Remove(existing);
            dictionaries.Insert(Math.Max(0, index), replacement);
        }
        else
        {
            dictionaries.Insert(0, replacement);
        }

        _colorDictionary = replacement;
        IsContrast = contrast;
        IsDark = contrast ? IsDarkColor(SystemColors.WindowColor) : dark;
        _log.Write(LogLevel.Info, contrast ? "Applied the Windows contrast theme." : $"Applied {(dark ? "dark" : "light")} theme.");
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Every app colour mapped to the colours of the active Windows contrast theme, so text, borders
    /// and buttons follow the user's own choice. A test keeps this list in step with Colors.Light.xaml.
    /// </summary>
    public static ResourceDictionary BuildContrastColors()
    {
        var window = SystemColors.WindowBrush;
        var text = SystemColors.WindowTextBrush;
        var highlight = SystemColors.HighlightBrush;
        var highlightText = SystemColors.HighlightTextBrush;
        var disabled = SystemColors.GrayTextBrush;
        var link = SystemColors.HotTrackBrush;

        return new ResourceDictionary
        {
            // Surfaces: the window colour everywhere, so nothing sits on an unexpected background.
            ["Brush.Background"] = window,
            ["Brush.Sidebar"] = window,
            ["Brush.Surface"] = window,
            ["Brush.SurfaceAlt"] = window,
            ["Brush.SurfaceHover"] = window,
            ["Brush.SurfacePressed"] = window,
            ["Brush.LogBackground"] = window,
            ["Brush.Overlay"] = Brushes.Transparent,

            // Text and borders: the window text colour; borders must stay visible.
            ["Brush.TextPrimary"] = text,
            ["Brush.TextSecondary"] = text,
            ["Brush.TextTertiary"] = disabled,
            ["Brush.Border"] = text,
            ["Brush.BorderStrong"] = text,

            // The accent becomes the selection colour, with its own text colour on top.
            ["Brush.Accent"] = highlight,
            ["Brush.AccentHover"] = highlight,
            ["Brush.AccentPressed"] = highlight,
            ["Brush.AccentText"] = highlightText,
            ["Brush.AccentSoft"] = window,

            // Selected items (current page, chosen toggle, selected row) use the selection colours,
            // so the choice stays visible.
            ["Brush.Selected"] = highlight,
            ["Brush.SelectedText"] = highlightText,
            ["Brush.SelectedItemText"] = highlightText,

            // Status colours are shown as text and icons; their soft backgrounds become plain.
            ["Brush.Danger"] = text,
            ["Brush.Warning"] = text,
            ["Brush.Success"] = text,
            ["Brush.Info"] = link,
            ["Brush.DangerSoft"] = window,
            ["Brush.WarningSoft"] = window,
            ["Brush.SuccessSoft"] = window,
            ["Brush.InfoSoft"] = window
        };
    }

    private static bool IsDarkColor(Color color) =>
        (0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B) < 128;

    public static bool IsSystemDark()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1);
            return value is int intValue && intValue == 0;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }
}
