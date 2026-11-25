// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.IO;
using System.Xml.Linq;
using Xunit;

namespace CtrlZ.App.Tests;

/// <summary>
/// Reads the app's XAML as text and checks what a screen reader and a keyboard user need: every
/// control a user can press or type into has a name, and inputs are reachable. Nothing is shown,
/// clicked or captured.
/// </summary>
public class AccessibilityTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static readonly string[] PressableTypes = ["Button", "ToggleButton", "RadioButton", "CheckBox"];
    private static readonly string[] InputTypes = ["TextBox", "PasswordBox", "ComboBox"];

    private static string AppFolder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "CtrlZ.App")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new DirectoryNotFoundException("Could not find src/CtrlZ.App above the test output.")
            : Path.Combine(directory.FullName, "src", "CtrlZ.App");
    }

    public static IEnumerable<object[]> XamlFiles() =>
        Directory.EnumerateFiles(AppFolder(), "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                           !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) &&
                           !path.Contains(Path.DirectorySeparatorChar + "Themes" + Path.DirectorySeparatorChar) &&
                           !path.EndsWith("App.xaml", StringComparison.OrdinalIgnoreCase))
            .Select(path => new object[] { Path.GetRelativePath(AppFolder(), path) });

    [Theory]
    [MemberData(nameof(XamlFiles))]
    public void Every_pressable_control_has_a_name_a_screen_reader_can_read(string file)
    {
        var document = XDocument.Load(Path.Combine(AppFolder(), file), LoadOptions.SetLineInfo);
        var unnamed = document.Descendants()
            .Where(element => element.Name.Namespace == Presentation && PressableTypes.Contains(element.Name.LocalName))
            .Where(element => !HasName(element))
            .Select(Describe)
            .ToList();

        Assert.True(unnamed.Count == 0, $"{file}: controls without a readable name:\n" + string.Join("\n", unnamed));
    }

    [Theory]
    [MemberData(nameof(XamlFiles))]
    public void Every_input_has_a_name_a_screen_reader_can_read(string file)
    {
        var document = XDocument.Load(Path.Combine(AppFolder(), file), LoadOptions.SetLineInfo);
        var unnamed = document.Descendants()
            .Where(element => element.Name.Namespace == Presentation && InputTypes.Contains(element.Name.LocalName))
            .Where(element => Attribute(element, "AutomationProperties.Name") is null &&
                              Attribute(element, "AutomationProperties.LabeledBy") is null)
            .Select(Describe)
            .ToList();

        Assert.True(unnamed.Count == 0, $"{file}: inputs without a readable name:\n" + string.Join("\n", unnamed));
    }

    [Fact]
    public void Every_window_closes_with_escape()
    {
        var missing = Directory.EnumerateFiles(Path.Combine(AppFolder(), "Views"), "*Window.xaml")
            .Where(path =>
            {
                var xaml = File.ReadAllText(path);
                var codeBehind = File.Exists(path + ".cs") ? File.ReadAllText(path + ".cs") : string.Empty;
                return !xaml.Contains("IsCancel=\"True\"", StringComparison.Ordinal) &&
                       !codeBehind.Contains("Key.Escape", StringComparison.Ordinal);
            })
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(missing.Count == 0, "Windows that Esc does not close: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_page_has_a_keyboard_shortcut()
    {
        var xaml = File.ReadAllText(Path.Combine(AppFolder(), "MainWindow.xaml"));
        foreach (var page in new[] { "scan", "apps", "install", "add", "catalog", "browsers", "ready", "updates", "tweaks", "settings" })
        {
            Assert.Contains($"CommandParameter=\"{page}\"", xaml);
        }
    }

    /// <summary>
    /// A control is named when it says so explicitly, or when its content is plain words (a Content
    /// attribute or a TextBlock inside it) rather than an icon glyph.
    /// </summary>
    private static bool HasName(XElement element)
    {
        if (Attribute(element, "AutomationProperties.Name") is not null ||
            Attribute(element, "AutomationProperties.LabeledBy") is not null)
        {
            return true;
        }

        if (Attribute(element, "Content") is { } content)
        {
            return !IsIconReference(content);
        }

        if (Attribute(element, "Style") is { } style && style.Contains("Toggle.Switch", StringComparison.Ordinal))
        {
            return false;
        }

        // Text children count as a name unless they are a glyph (the icon font, or an Icon.* resource).
        var readableText = element.Descendants()
            .Where(child => child.Name.LocalName is "TextBlock" or "Run" or "AccessText")
            .Any(child => Attribute(child, "Text") is { } text && !IsIconReference(text) && !IsIconFont(child));
        return readableText || (!element.HasElements && !string.IsNullOrWhiteSpace(element.Value));
    }

    private static bool IsIconFont(XElement element) =>
        Attribute(element, "FontFamily")?.Contains("Font.Icons", StringComparison.Ordinal) == true;

    private static bool IsIconReference(string value) =>
        value.Contains("Icon.", StringComparison.Ordinal) && value.StartsWith('{');

    private static string? Attribute(XElement element, string name) =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == name)?.Value;

    private static string Describe(XElement element)
    {
        var line = ((System.Xml.IXmlLineInfo)element).LineNumber;
        var hint = Attribute(element, "Command") ?? Attribute(element, "Click") ?? Attribute(element, "ToolTip") ??
                   Attribute(element, "Text") ?? Attribute(element, "IsChecked") ?? Attribute(element, "Name") ?? string.Empty;
        return $"  line {line}: <{element.Name.LocalName}> {hint}";
    }

    /// <summary>
    /// A list item's screen-reader name comes from its object's <c>ToString()</c>. Without an
    /// override, Narrator reads the .NET type name ("CtrlZ.App.ViewModels.NavItem") for every row.
    /// </summary>
    [Fact]
    public void Every_list_row_has_a_readable_name()
    {
        var rows = typeof(CtrlZ.App.ViewModels.NavItem).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false, IsPublic: true } &&
                           type.Namespace == "CtrlZ.App.ViewModels" &&
                           (type.Name.EndsWith("Row", StringComparison.Ordinal) ||
                            type.Name.EndsWith("Item", StringComparison.Ordinal) ||
                            type.Name.EndsWith("RowViewModel", StringComparison.Ordinal) ||
                            type.Name.EndsWith("ItemViewModel", StringComparison.Ordinal) ||
                            type.Name.EndsWith("ChoiceViewModel", StringComparison.Ordinal)))
            .ToList();

        Assert.True(rows.Count >= 15, $"Only {rows.Count} row types found; the name filter may be out of date.");
        var missing = rows
            .Where(type => type.GetMethod(nameof(ToString), Type.EmptyTypes)!.DeclaringType != type)
            .Select(type => type.Name)
            .ToList();
        Assert.True(missing.Count == 0, "Rows read as their type name: " + string.Join(", ", missing));
    }
}
