// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.App.Localization;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using CtrlZ.App.ViewModels;

namespace CtrlZ.App;

public partial class MainWindow : Window
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;

    private MainViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        Services.TextScale.Apply(this);
        SourceInitialized += (_, _) =>
        {
            ApplyWindowAppearance();
            RestoreWindowBounds();
        };
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
                _viewModel.Theme.ThemeChanged -= OnThemeChanged;
            }

            _viewModel = DataContext as MainViewModel;
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
                _viewModel.Theme.ThemeChanged += OnThemeChanged;
            }
        };
        StateChanged += (_, _) => UpdateMaximizeGlyph();
        Closing += OnClosing;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentPage))
        {
            // Respect "Animation effects" being off in Windows settings: switch pages without a fade.
            if (!SystemParameters.ClientAreaAnimation)
            {
                PageHost.BeginAnimation(OpacityProperty, null);
                PageHost.Opacity = 1;
                return;
            }

            var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            PageHost.BeginAnimation(OpacityProperty, animation);
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyWindowAppearance();

    private void ApplyWindowAppearance()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var dark = _viewModel?.Theme.IsDark == true ? 1 : 0;
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
            var corner = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corner, sizeof(int));
        }
        catch
        {
            // Older Windows builds do not support these attributes.
        }
    }

    private void RestoreWindowBounds()
    {
        if (_viewModel is null)
        {
            return;
        }

        var settings = _viewModel.State.Settings;
        if (settings.WindowWidth >= MinWidth && settings.WindowWidth <= SystemParameters.VirtualScreenWidth &&
            settings.WindowHeight >= MinHeight && settings.WindowHeight <= SystemParameters.VirtualScreenHeight)
        {
            Width = settings.WindowWidth;
            Height = settings.WindowHeight;
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        UpdateMaximizeGlyph();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateMaximizeGlyph()
    {
        if (MaximizeButton is null)
        {
            return;
        }

        MaximizeGlyph.Text = (string)(WindowState == WindowState.Maximized
            ? FindResource("Icon.Restore")
            : FindResource("Icon.Maximize"));
        MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? Loc.Get("Main_Restore") : Loc.Get("Main_Maximize2");
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.State.Settings.WindowWidth = ActualWidth;
            _viewModel.State.Settings.WindowHeight = ActualHeight;
            _viewModel.State.SaveSettings();
            _viewModel.Theme.ThemeChanged -= OnThemeChanged;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
