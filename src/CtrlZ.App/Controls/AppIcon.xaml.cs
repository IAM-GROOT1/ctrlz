// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CtrlZ.App.Services;

namespace CtrlZ.App.Controls;

/// <summary>
/// An app's icon. In order: the icon file it was scanned with; the icon of a program with the same
/// name installed on this PC; the app's full-colour icon bundled with CTRL+Z; its one-colour brand
/// logo bundled with CTRL+Z; its coloured initials.
/// </summary>
public partial class AppIcon : UserControl
{
    public static readonly DependencyProperty IconPathProperty = DependencyProperty.Register(
        nameof(IconPath), typeof(string), typeof(AppIcon),
        new PropertyMetadata(null, (d, _) => ((AppIcon)d).Refresh()));

    public static readonly DependencyProperty FallbackTextProperty = DependencyProperty.Register(
        nameof(FallbackText), typeof(string), typeof(AppIcon),
        new PropertyMetadata(string.Empty, (d, _) => ((AppIcon)d).Refresh()));

    /// <summary>The app's winget id, if it has one; the surest way to find its brand logo.</summary>
    public static readonly DependencyProperty PackageIdProperty = DependencyProperty.Register(
        nameof(PackageId), typeof(string), typeof(AppIcon),
        new PropertyMetadata(null, (d, _) => ((AppIcon)d).Refresh()));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(AppIcon),
        new PropertyMetadata(32d, (d, _) => ((AppIcon)d).RefreshSize()));

    public AppIcon()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            InstalledIcons.Loaded += OnInstalledIconsLoaded;
            RefreshSize();
            Refresh();
        };
        Unloaded += (_, _) => InstalledIcons.Loaded -= OnInstalledIconsLoaded;
    }

    public string? IconPath
    {
        get => (string?)GetValue(IconPathProperty);
        set => SetValue(IconPathProperty, value);
    }

    public string FallbackText
    {
        get => (string)GetValue(FallbackTextProperty);
        set => SetValue(FallbackTextProperty, value);
    }

    public string? PackageId
    {
        get => (string?)GetValue(PackageIdProperty);
        set => SetValue(PackageIdProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    /// <summary>What is showing: "file", "installed", "catalog", "brand" or "initials" (for tests).</summary>
    public string Showing { get; private set; } = "initials";

    private void OnInstalledIconsLoaded(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(Refresh);

    private void RefreshSize()
    {
        Width = Size;
        Height = Size;
        if (Root is null)
        {
            return;
        }

        Root.Width = Size;
        Root.Height = Size;
        Initials.FontSize = Math.Max(9, Size * 0.34);
        var corner = new CornerRadius(Math.Max(4, Size * 0.22));
        Fallback.CornerRadius = corner;
        BrandTile.CornerRadius = corner;
        BrandGlyph.Margin = new Thickness(Math.Round(Size * 0.2));
    }

    private void Refresh()
    {
        if (Fallback is null || IconImage is null)
        {
            return;
        }

        var text = FallbackText;
        var image = IconLoader.TryLoad(IconPath);
        Showing = image is null ? "initials" : "file";
        if (image is null)
        {
            image = IconLoader.TryLoad(InstalledIcons.Find(text));
            Showing = image is null ? "initials" : "installed";
        }

        if (image is null)
        {
            image = CatalogIcons.Find(PackageId, text);
            Showing = image is null ? "initials" : "catalog";
        }

        var brand = image is null ? BrandIcons.Find(PackageId, text) : null;
        if (brand is not null)
        {
            Showing = "brand";
            BrandTile.Background = brand.Background;
            BrandGlyph.Fill = brand.Glyph;
            BrandGlyph.Data = brand.Shape;
        }

        if (image is null && brand is null)
        {
            Initials.Text = BuildInitials(text);
            Initials.Foreground = Brushes.White;
            Fallback.Background = IconLoader.FallbackBrush(text);
        }

        IconImage.Source = image;
        IconImage.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        BrandTile.Visibility = brand is null ? Visibility.Collapsed : Visibility.Visible;
        Fallback.Visibility = image is null && brand is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string BuildInitials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "?";
        }

        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Any(char.IsLetterOrDigit))
            .Take(2)
            .ToArray();
        if (words.Length == 0)
        {
            return "?";
        }

        return string.Concat(words.Select(word => char.ToUpperInvariant(word.First(char.IsLetterOrDigit))));
    }
}
