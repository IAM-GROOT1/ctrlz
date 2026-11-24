// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using CtrlZ.App.Services;
using Xunit;

namespace CtrlZ.App.Tests;

/// <summary>Under a Windows contrast theme every app colour comes from the user's system colours.</summary>
public class ContrastThemeTests
{
    private static string ThemesFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var themes = Path.Combine(dir.FullName, "src", "CtrlZ.App", "Themes");
            if (Directory.Exists(themes))
            {
                return themes;
            }
        }

        throw new DirectoryNotFoundException("Could not find src/CtrlZ.App/Themes above the test output.");
    }

    private static IReadOnlyList<string> BrushKeys(string file) =>
        Regex.Matches(File.ReadAllText(Path.Combine(ThemesFolder(), file)), @"x:Key=""(?<key>Brush\.[A-Za-z.]+)""")
            .Select(m => m.Groups["key"].Value).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList();

    [Fact]
    public void The_contrast_palette_defines_every_app_colour_and_only_those()
    {
        var contrast = ThemeService.BuildContrastColors().Keys.Cast<string>().OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.Equal(BrushKeys("Colors.Light.xaml"), contrast);
        Assert.Equal(BrushKeys("Colors.Dark.xaml"), contrast);
    }

    [Fact]
    public void Text_and_buttons_use_the_system_text_and_selection_colours()
    {
        var colors = ThemeService.BuildContrastColors();

        Assert.Equal(SystemColors.WindowTextBrush.Color, ((SolidColorBrush)colors["Brush.TextPrimary"]).Color);
        Assert.Equal(SystemColors.WindowBrush.Color, ((SolidColorBrush)colors["Brush.Surface"]).Color);
        Assert.Equal(SystemColors.HighlightBrush.Color, ((SolidColorBrush)colors["Brush.Accent"]).Color);
        Assert.Equal(SystemColors.HighlightTextBrush.Color, ((SolidColorBrush)colors["Brush.AccentText"]).Color);
        Assert.Equal(SystemColors.WindowTextBrush.Color, ((SolidColorBrush)colors["Brush.Border"]).Color);
    }
}
