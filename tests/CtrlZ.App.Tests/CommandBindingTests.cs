// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace CtrlZ.App.Tests;

/// <summary>
/// WPF ignores a binding to a command that does not exist, so a button can silently do nothing.
/// This reads every view and checks that each bound <c>XCommand</c> has a matching
/// <c>[RelayCommand]</c> method <c>X</c> or <c>XAsync</c> (or an explicit property) in the view-models.
/// </summary>
public partial class CommandBindingTests
{
    private static string AppFolder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "CtrlZ.App")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory!.FullName, "src", "CtrlZ.App");
    }

    [Fact]
    public void Every_bound_command_exists()
    {
        var folder = AppFolder();
        var viewModels = string.Join("\n", Directory.EnumerateFiles(Path.Combine(folder, "ViewModels"), "*.cs")
            .Concat(Directory.EnumerateFiles(Path.Combine(folder, "Services"), "*.cs"))
            .Select(File.ReadAllText));
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in RelayMethod().Matches(viewModels))
        {
            var name = match.Groups["name"].Value;
            known.Add(name.EndsWith("Async", StringComparison.Ordinal) ? name[..^5] : name);
        }

        foreach (Match match in CommandProperty().Matches(viewModels))
        {
            known.Add(match.Groups["name"].Value);
        }

        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.xaml", SearchOption.AllDirectories)
                     .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)))
        {
            foreach (Match match in BoundCommand().Matches(File.ReadAllText(file)))
            {
                if (!known.Contains(match.Groups["name"].Value))
                {
                    missing.Add($"{Path.GetFileName(file)}: {match.Groups["name"].Value}Command");
                }
            }
        }

        Assert.True(missing.Count == 0, "Bound commands with no view-model method:\n" + string.Join("\n", missing.Distinct()));
    }

    [GeneratedRegex(@"\[RelayCommand[^\]]*\]\s*(?:\[[^\]]*\]\s*)*(?:public|private|internal|protected)[^(]*?\s(?<name>\w+)\s*\(")]
    private static partial Regex RelayMethod();

    [GeneratedRegex(@"ICommand\??\s+(?<name>\w+)Command\b")]
    private static partial Regex CommandProperty();

    // Only Command="..." attributes: a property such as PreviewCommand bound to Text is not an ICommand.
    [GeneratedRegex(@"\bCommand=""\{Binding\s+(?:Path=)?(?:DataContext\.)?(?<name>\w+)Command\b")]
    private static partial Regex BoundCommand();
}
