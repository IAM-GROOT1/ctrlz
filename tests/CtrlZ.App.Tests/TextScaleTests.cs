// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CtrlZ.App.Services;
using Xunit;

namespace CtrlZ.App.Tests;

/// <summary>Windows' Text size setting scales every window, without leaving the screen.</summary>
public class TextScaleTests
{
    [Theory]
    [InlineData(null, 1.0)]
    [InlineData(100, 1.0)]
    [InlineData(150, 1.5)]
    [InlineData(225, 2.25)]
    [InlineData(400, 2.25)]
    [InlineData(80, 1.0)]
    public void The_setting_becomes_a_factor(int? percent, double expected)
    {
        Assert.Equal(expected, TextScale.FromSetting(percent));
        Assert.Equal(1.0, TextScale.FromSetting("150"));
    }

    [Fact]
    public void A_window_scales_its_content_and_size_within_the_work_area()
    {
        RunOnSta(() =>
        {
            var root = new Grid();
            var window = new Window { Width = 800, Height = 600, MinWidth = 400, MinHeight = 300, Content = root };

            TextScale.Apply(window, 1.5, new Size(1000, 700));

            var scale = Assert.IsType<ScaleTransform>(root.LayoutTransform);
            Assert.Equal(1.5, scale.ScaleX);
            Assert.Equal(1000, window.Width);   // 1200, kept on the screen
            Assert.Equal(700, window.Height);   // 900, kept on the screen
            Assert.Equal(600, window.MinWidth);
            Assert.Equal(450, window.MinHeight);

            var unscaled = new Window { Width = 800, Content = new Grid() };
            TextScale.Apply(unscaled, 1.0, new Size(1000, 700));
            Assert.Equal(800, unscaled.Width);
            Assert.Null(((Grid)unscaled.Content).LayoutTransform as ScaleTransform);

            // The main window (1180 x 760, at least 980 x 640) on a screen whose work area is
            // 1366 x 720: it fits at 100 % too, instead of reaching under the taskbar.
            var main = new Window { Width = 1180, Height = 760, MinWidth = 980, MinHeight = 640, Content = new Grid() };
            TextScale.Apply(main, 1.0, new Size(1366, 720));
            Assert.Equal(1180, main.Width);
            Assert.Equal(720, main.Height);
            Assert.Equal(640, main.MinHeight);

            var tiny = new Window { Width = 1180, Height = 760, MinWidth = 980, MinHeight = 640, Content = new Grid() };
            TextScale.Apply(tiny, 1.0, new Size(800, 600));
            Assert.Equal(800, tiny.Width);
            Assert.Equal(800, tiny.MinWidth);
            Assert.Equal(600, tiny.MinHeight);
        });
    }

    [Fact]
    public void Every_window_applies_the_text_size()
    {
        var app = Path.Combine(SrcFolder(), "CtrlZ.App");
        var windows = Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories)
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Where(p => File.ReadAllText(p).TrimStart().Contains("<Window ", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(windows);
        var missing = windows.Where(p => !File.ReadAllText(p + ".cs").Contains("TextScale.Apply(this)", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();
        Assert.True(missing.Count == 0, "Call Services.TextScale.Apply(this) after InitializeComponent in:\n" + string.Join("\n", missing));
    }

    private static string SrcFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "CtrlZ.App")))
            {
                return Path.Combine(dir.FullName, "src");
            }
        }

        throw new DirectoryNotFoundException("Could not find src above the test output.");
    }

    private static void RunOnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
