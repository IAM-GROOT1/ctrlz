// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Models;

namespace CtrlZ.Core.Install;

/// <summary>The command editor's raw fields, exactly as typed.</summary>
public sealed record PostInstallCommandInput(
    string Executable,
    string ArgumentsText,
    string WorkingDirectory,
    string TimeoutText,
    bool RequiresElevation,
    bool Enabled);

/// <summary>Result of reading the editor: a command, no command (blank form), or an error.</summary>
public sealed record PostInstallCommandParse(PostInstallCommand? Command, string? Error)
{
    public bool IsValid => Error is null;
}

/// <summary>
/// Turns the command editor's fields into a <see cref="PostInstallCommand"/> and back. Pure: it
/// never saves, approves or runs anything. Editing changes the command's hash, so an existing
/// approval stops matching on its own.
/// </summary>
public static class PostInstallCommandForm
{
    public const int MinTimeoutSeconds = 5;
    public const int MaxTimeoutSeconds = 600;

    /// <summary>A blank form: no program, elevation off, enabled when filled in.</summary>
    public static PostInstallCommandInput Empty { get; } = new(string.Empty, string.Empty, string.Empty, "60", false, true);

    /// <summary>Commands can be added or edited only in a profile that originated on this PC.</summary>
    public static bool CanEdit(ProfileOrigin origin) => origin == ProfileOrigin.Local;

    public static PostInstallCommandInput From(PostInstallCommand? command) => command is null
        ? Empty
        : new PostInstallCommandInput(
            command.Executable,
            string.Join(Environment.NewLine, command.Arguments),
            command.WorkingDirectory ?? string.Empty,
            command.TimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            command.RequiresElevation,
            command.Enabled);

    /// <summary>True when the editor fields still show the stored command exactly.</summary>
    public static bool IsUnchanged(PostInstallCommand? stored, PostInstallCommandInput input) =>
        From(stored).Equals(input with { ArgumentsText = input.ArgumentsText?.Replace("\r\n", "\n").Replace("\n", Environment.NewLine) ?? string.Empty });

    public static PostInstallCommandParse Parse(PostInstallCommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var executable = (input.Executable ?? string.Empty).Trim();
        var arguments = (input.ArgumentsText ?? string.Empty)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        if (executable.Length == 0 && arguments.Count == 0)
        {
            return new PostInstallCommandParse(null, null);
        }

        if (executable.Length == 0)
        {
            return Fail("Enter the program to run, or clear the arguments to remove the command.");
        }

        if (executable.Contains('\n') || executable.Contains('\r'))
        {
            return Fail("The program must be on one line. Put each argument on its own line below.");
        }

        if (!int.TryParse((input.TimeoutText ?? string.Empty).Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var timeout) ||
            timeout < MinTimeoutSeconds || timeout > MaxTimeoutSeconds)
        {
            return Fail($"Timeout must be a whole number of seconds from {MinTimeoutSeconds} to {MaxTimeoutSeconds}.");
        }

        var directory = (input.WorkingDirectory ?? string.Empty).Trim();
        if (directory.Length > 0 && !Path.IsPathFullyQualified(directory))
        {
            return Fail("Working directory must be a full path, such as C:\\Tools, or left blank.");
        }

        return new PostInstallCommandParse(new PostInstallCommand
        {
            Executable = executable,
            Arguments = arguments,
            WorkingDirectory = directory.Length == 0 ? null : directory,
            TimeoutSeconds = timeout,
            RequiresElevation = input.RequiresElevation,
            Enabled = input.Enabled
        }, null);
    }

    /// <summary>
    /// A risky command needs a second, explicit confirmation when it is saved with new text.
    /// An unchanged command was confirmed when it was saved; removing one needs nothing.
    /// </summary>
    public static bool NeedsRiskConfirmation(PostInstallCommand? before, PostInstallCommand? after)
    {
        if (after is null || PostInstallCommandRisk.Scan(after).Count == 0)
        {
            return false;
        }

        return before is null ||
               !string.Equals(PostInstallApprovalStore.HashOf(before), PostInstallApprovalStore.HashOf(after), StringComparison.Ordinal);
    }

    private static PostInstallCommandParse Fail(string message) => new(null, message);
}
