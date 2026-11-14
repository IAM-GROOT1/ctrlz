using System.Windows;
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
        if (_disposed || _mode != ThemeMode.System)
        {
            return;
        }

        try
        {
            _application.Dispatcher.BeginInvoke(() => Apply(ThemeMode.System));
        }
        catch (Exception ex)
        {
            _log.Write(LogLevel.Debug, "Could not apply a Windows theme change.", ex);
        }
    }

    public void Apply(bool dark)
    {
        var source = new Uri(dark ? "Themes/Colors.Dark.xaml" : "Themes/Colors.Light.xaml", UriKind.Relative);
        var dictionaries = _application.Resources.MergedDictionaries;
        var replacement = new ResourceDictionary { Source = source };
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
        IsDark = dark;
        _log.Write(LogLevel.Info, $"Applied {(dark ? "dark" : "light")} theme.");
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

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
