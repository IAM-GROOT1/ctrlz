using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CtrlZ.App.Services;

namespace CtrlZ.App.Controls;

public partial class AppIcon : UserControl
{
    public static readonly DependencyProperty IconPathProperty = DependencyProperty.Register(
        nameof(IconPath), typeof(string), typeof(AppIcon),
        new PropertyMetadata(null, (d, _) => ((AppIcon)d).Refresh()));

    public static readonly DependencyProperty FallbackTextProperty = DependencyProperty.Register(
        nameof(FallbackText), typeof(string), typeof(AppIcon),
        new PropertyMetadata(string.Empty, (d, _) => ((AppIcon)d).Refresh()));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(AppIcon),
        new PropertyMetadata(32d, (d, _) => ((AppIcon)d).RefreshSize()));

    public AppIcon()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            RefreshSize();
            Refresh();
        };
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

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

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
        Fallback.CornerRadius = new CornerRadius(Math.Max(4, Size * 0.22));
    }

    private void Refresh()
    {
        if (Fallback is null || IconImage is null)
        {
            return;
        }

        var text = FallbackText;
        Initials.Text = BuildInitials(text);
        Initials.Foreground = Brushes.White;
        Fallback.Background = IconLoader.FallbackBrush(text);

        var image = IconLoader.TryLoad(IconPath);
        IconImage.Source = image;
        IconImage.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        Fallback.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
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
