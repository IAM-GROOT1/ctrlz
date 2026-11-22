// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

namespace CtrlZ.Core.Cli;

/// <summary>Process exit codes of the command line. Distinct so scripts can branch on them.</summary>
public static class CliExitCode
{
    public const int Success = 0;
    public const int Usage = 1;
    public const int PartialFailure = 2;
    public const int InvalidProfile = 3;
    public const int DependencyFailure = 4;
    public const int Cancelled = 5;
    public const int NeedsAdmin = 6;
    public const int Busy = 7;
    public const int Unexpected = 8;
}

public enum CliCommand
{
    Help,
    Install,
    DryRun,
    List
}

/// <summary>Parsed command line.</summary>
public sealed record CliOptions(
    CliCommand Command,
    string? ProfilePath = null,
    bool Silent = false,
    bool Yes = false,
    bool AllowCommands = false,
    bool AllowRisky = false,
    string? JsonPath = null,
    bool ContinueWithoutAdmin = false);

public sealed record CliParseResult(CliOptions? Options, string? Error)
{
    public bool IsValid => Error is null && Options is not null;
}

public static class CliParser
{
    public const string Usage =
        """
        Usage:
          ctrlz install <profile.json> [--yes] [--silent] [--allow-commands] [--allow-risky]
                        [--continue-without-admin] [--json <report.json>]
          ctrlz dry-run <profile.json> [--silent] [--json <report.json>]
          ctrlz list
          ctrlz help

        install          Prints the full plan, including every post-install command. Installs only with --yes.
        --yes            Confirms the printed plan. Without it nothing is installed (exit code 5).
        --allow-commands With --yes: approves the printed post-install commands of a profile that did
                         not originate on this PC. This is your explicit consent for exactly those commands.
        --allow-risky    Allows commands that match a risky pattern. Asked on every run.
        --continue-without-admin  Installs without administrator rights; machine-wide installers may fail.
        --silent         Prints the plan and the summary only, no progress lines.
        --json <path>    Also writes a machine-readable report.

        Exit codes: 0 success, 1 usage, 2 some apps failed, 3 invalid profile, 4 a dependency failed,
        5 not confirmed or cancelled, 6 needs administrator rights, 7 another install is running,
        8 unexpected error (see the log).
        """;

    public static CliParseResult Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0)
        {
            return new CliParseResult(new CliOptions(CliCommand.Help), null);
        }

        var command = args[0].ToLowerInvariant() switch
        {
            "install" => CliCommand.Install,
            "dry-run" or "dryrun" => CliCommand.DryRun,
            "list" => CliCommand.List,
            "help" or "--help" or "-h" or "/?" => CliCommand.Help,
            _ => (CliCommand?)null
        };

        if (command is null)
        {
            return Fail($"Unknown command '{args[0]}'.");
        }

        string? path = null;
        bool silent = false, yes = false, allowCommands = false, allowRisky = false, noAdmin = false;
        string? json = null;

        for (var index = 1; index < args.Count; index++)
        {
            var arg = args[index];
            switch (arg.ToLowerInvariant())
            {
                case "--silent":
                    silent = true;
                    break;
                case "--yes":
                case "-y":
                    yes = true;
                    break;
                case "--allow-commands":
                    allowCommands = true;
                    break;
                case "--continue-without-admin":
                    noAdmin = true;
                    break;
                case "--allow-risky":
                    allowRisky = true;
                    break;
                case "--json":
                    if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        return Fail("--json needs a file path.");
                    }

                    json = args[++index];
                    break;
                default:
                    if (arg.StartsWith('-'))
                    {
                        return Fail($"Unknown option '{arg}'.");
                    }

                    if (path is not null)
                    {
                        return Fail("Give exactly one profile file.");
                    }

                    path = arg;
                    break;
            }
        }

        if (command is CliCommand.Install or CliCommand.DryRun && string.IsNullOrWhiteSpace(path))
        {
            return Fail("Give the profile file to use.");
        }

        if (command is CliCommand.List or CliCommand.Help && (path is not null || json is not null || yes || allowCommands || allowRisky || noAdmin))
        {
            return Fail($"'{args[0]}' takes no profile or options.");
        }

        if (command == CliCommand.DryRun && (yes || allowCommands || allowRisky || noAdmin))
        {
            return Fail("--yes, --allow-commands and --allow-risky apply to install only; a dry run never installs or runs anything.");
        }

        return new CliParseResult(new CliOptions(command.Value, path, silent, yes, allowCommands, allowRisky, json, noAdmin), null);
    }

    private static CliParseResult Fail(string error) => new(null, error);
}
