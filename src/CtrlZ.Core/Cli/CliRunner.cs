// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Text.Json;
using CtrlZ.Core.Install;
using CtrlZ.Core.Models;
using CtrlZ.Core.Safety;
using CtrlZ.Core.Services;

namespace CtrlZ.Core.Cli;

/// <summary>Everything the command line touches, injectable so every path is testable.</summary>
public sealed class CliEnvironment
{
    public required TextWriter Out { get; init; }
    public required TextWriter Error { get; init; }
    public required AppSettings Settings { get; init; }
    public required ProfileStore Profiles { get; init; }
    public required ProfileOriginStore Origins { get; init; }
    public required PostInstallApprovalStore Approvals { get; init; }
    public required PostInstallFirstRunStore FirstRun { get; init; }
    public required InstallEngine Engine { get; init; }
    public required Func<bool> IsElevated { get; init; }
    public required Func<InstallLock?> AcquireLock { get; init; }
    public required Func<DryRunRunner> CreateDryRun { get; init; }
    public Func<string, bool>? FileExists { get; init; }
    public Func<string, bool?>? IsSigned { get; init; }
}

/// <summary>
/// The command line. Same rules as the app: every profile is loaded through
/// <see cref="ProfileOriginRecorder.LoadForUse"/>, the gate decides at run time, nothing installs
/// without --yes, and commands of a profile from outside this PC stay blocked unless the user
/// approves exactly the printed commands with --allow-commands and --yes.
/// </summary>
public sealed class CliRunner
{
    private readonly CliEnvironment _env;

    public CliRunner(CliEnvironment environment)
    {
        _env = environment;
    }

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        var parsed = CliParser.Parse(args);
        if (!parsed.IsValid)
        {
            _env.Error.WriteLine(parsed.Error);
            _env.Error.WriteLine();
            _env.Error.WriteLine(CliParser.Usage);
            return CliExitCode.Usage;
        }

        var options = parsed.Options!;
        return options.Command switch
        {
            CliCommand.Help => Help(),
            CliCommand.List => List(),
            CliCommand.DryRun => await DryRunAsync(options, cancellationToken).ConfigureAwait(false),
            _ => await InstallAsync(options, cancellationToken).ConfigureAwait(false)
        };
    }

    private int Help()
    {
        _env.Out.WriteLine(CliParser.Usage);
        return CliExitCode.Success;
    }

    private int List()
    {
        var profiles = _env.Profiles.List();
        if (profiles.Count == 0)
        {
            _env.Out.WriteLine("No saved profiles.");
            return CliExitCode.Success;
        }

        foreach (var profile in profiles)
        {
            var origin = _env.Origins.Get(profile.Id) == ProfileOrigin.Local ? "created on this PC" : "from outside this PC";
            _env.Out.WriteLine($"{profile.Name}  ({profile.AppCount} apps, {origin})");
            _env.Out.WriteLine($"  {profile.Path}");
        }

        return CliExitCode.Success;
    }

    private AppProfile? Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                _env.Error.WriteLine($"Profile not found: {path}");
                return null;
            }

            // The only way a profile enters: origin is recorded, never read from the file.
            var profile = ProfileOriginRecorder.LoadForUse(_env.Origins, _env.Profiles.Load(path));
            if (!profile.Apps.Any(app => app.Enabled))
            {
                _env.Error.WriteLine("The profile has no enabled apps.");
                return null;
            }

            return profile;
        }
        catch (Exception ex)
        {
            _env.Error.WriteLine($"The profile could not be read: {ex.Message}");
            return null;
        }
    }

    private InstallPlan Plan(AppProfile profile) =>
        new InstallPlanner(_env.FileExists, _env.IsSigned, _env.Engine.Catalog).Build(profile, profile.Name);

    private IReadOnlyList<CommandPreviewLine> Commands(InstallPlan plan, AppProfile profile) =>
        CommandPreview.ForPlan(plan, profile, _env.Origins.Get(profile.Id), _env.Approvals);

    private void PrintPlan(AppProfile profile, InstallPlan plan, IReadOnlyList<CommandPreviewLine> commands)
    {
        var origin = _env.Origins.Get(profile.Id) == ProfileOrigin.Local
            ? "created on this PC"
            : "from outside this PC: its commands need your approval";
        _env.Out.WriteLine($"Profile: {profile.Name} ({origin})");
        _env.Out.WriteLine($"Plan: {plan.Count} app{(plan.Count == 1 ? string.Empty : "s")}, {plan.AdminCount} may need admin");

        var byApp = commands.GroupBy(line => line.AppId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var item in plan.Items)
        {
            var admin = item.RequiresAdmin ? "  [may need admin]" : string.Empty;
            _env.Out.WriteLine($"  {item.Order}. {item.Name} - {item.SourceDescription}{admin}");
            if (item.NotInstalledReason is not null)
            {
                _env.Out.WriteLine($"       will not install: {item.NotInstalledReason}");
            }

            if (byApp.TryGetValue(item.AppId, out var line))
            {
                _env.Out.WriteLine($"       after install runs: {line.CommandText}");
                _env.Out.WriteLine($"       {line.StateText}");
            }
        }

        foreach (var warning in plan.PlanWarnings.Skip(1))
        {
            _env.Out.WriteLine($"  note: {warning}");
        }
    }

    private async Task<int> DryRunAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var profile = Load(options.ProfilePath!);
        if (profile is null)
        {
            return CliExitCode.InvalidProfile;
        }

        var plan = Plan(profile);
        var commands = Commands(plan, profile);
        PrintPlan(profile, plan, commands);

        var progress = new Progress<string>(message =>
        {
            if (!options.Silent)
            {
                _env.Out.WriteLine("  " + message);
            }
        });
        var report = (await _env.CreateDryRun()
            .RunAsync(plan, profile.Apps.ToDictionary(app => app.Id), progress, cancellationToken)
            .ConfigureAwait(false)) with { Commands = commands };

        var text = report.WithExportNotes(profile.Apps, _env.Settings).ToText();
        _env.Out.WriteLine();
        _env.Out.Write(text);

        var exit = report.HasFailures ? CliExitCode.PartialFailure : CliExitCode.Success;
        WriteJson(options, "dry-run", profile, exit, report.Items.Select(item =>
            new ReportItem(item.AppId, item.Name, item.Status.ToString(), item.Message, NoteFor(profile, item.AppId))), commands);
        return exit;
    }

    private async Task<int> InstallAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var profile = Load(options.ProfilePath!);
        if (profile is null)
        {
            return CliExitCode.InvalidProfile;
        }

        using var runLock = _env.AcquireLock();
        if (runLock is null)
        {
            _env.Error.WriteLine("Another CTRL+Z install is running on this PC. Try again when it has finished.");
            return CliExitCode.Busy;
        }

        var plan = Plan(profile);
        var commands = Commands(plan, profile);
        PrintPlan(profile, plan, commands);
        _env.Out.WriteLine();

        // --allow-commands with --yes is explicit consent for exactly the commands printed above:
        // it records the same per-command approval the review screen writes, never the risky ones
        // without --allow-risky. Every check below runs on that prospective state, and the
        // approvals are written only once all checks have passed, so a refused run changes nothing.
        var toApprove = new List<(AppEntry App, PostInstallCommand Command)>();
        if (options.AllowCommands && options.Yes)
        {
            foreach (var line in commands.Where(line =>
                         !line.WillRun &&
                         line.State is CommandRunState.Blocked or CommandRunState.NeedsReview &&
                         (!line.Risky || options.AllowRisky)))
            {
                var app = profile.Apps.First(candidate => candidate.Id == line.AppId);
                if (app.PostInstall is { Enabled: true } command &&
                    PostInstallApprovalStore.HashOf(command) == line.Hash)
                {
                    toApprove.Add((app, command));
                }
            }

            if (toApprove.Count > 0)
            {
                var origin = _env.Origins.Get(profile.Id);
                commands = CommandPreview.ForPlan(plan, profile, origin, (app, command) =>
                    toApprove.Any(pending => ReferenceEquals(pending.App, app) && ReferenceEquals(pending.Command, command))
                        ? ApprovalState.Current
                        : _env.Approvals.StateOf(profile.Id, app.Id, command));
            }
        }
        else if (options.AllowCommands)
        {
            _env.Out.WriteLine("--allow-commands has no effect without --yes.");
        }

        var risky = commands.Where(line => line.WillRun && line.Risky).ToList();
        if (risky.Count > 0 && !options.AllowRisky)
        {
            _env.Error.WriteLine("These commands match a risky pattern and would run:");
            foreach (var line in risky)
            {
                _env.Error.WriteLine($"  {line.AppName}: {line.CommandText}");
            }

            _env.Error.WriteLine("Nothing was installed. Re-run with --allow-risky only if you understand them.");
            return CliExitCode.Cancelled;
        }

        var adminApps = plan.Items.Where(item => item.NotInstalledReason is null && item.RequiresAdmin).Select(item => item.Name)
            .Concat(commands.Where(line => line.WillRun &&
                profile.Apps.First(app => app.Id == line.AppId).PostInstall!.RequiresElevation).Select(line => line.AppName + " (command)"))
            .ToList();
        if (adminApps.Count > 0 && !_env.IsElevated() && !options.ContinueWithoutAdmin)
        {
            _env.Error.WriteLine("Administrator rights are likely needed for: " + string.Join(", ", adminApps) + ".");
            _env.Error.WriteLine("Nothing was installed. Run from an elevated terminal, or add --continue-without-admin " +
                                 "(machine-wide installers may then fail).");
            return CliExitCode.NeedsAdmin;
        }

        if (!options.Yes)
        {
            _env.Out.WriteLine("Nothing was installed. Review the plan above, then re-run with --yes to install.");
            return CliExitCode.Cancelled;
        }

        // Every check passed: record the consent given with --allow-commands, then re-read.
        foreach (var (app, command) in toApprove)
        {
            _env.Approvals.Approve(profile.Id, app.Id, command);
        }

        if (toApprove.Count > 0)
        {
            _env.Out.WriteLine($"Approved {toApprove.Count} printed command{(toApprove.Count == 1 ? string.Empty : "s")} for this profile on this PC (--allow-commands).");
            commands = Commands(plan, profile);
        }

        // --yes is the first-run confirmation for the commands listed above.
        var willRun = commands.Where(line => line.WillRun).ToList();
        if (willRun.Count > 0)
        {
            _env.FirstRun.Record(profile.Id, willRun.Select(line => line.Hash));
        }

        var state = _env.Engine.CreateState(profile, Path.GetFullPath(options.ProfilePath!));
        InstallState finished;
        try
        {
            finished = await _env.Engine
                .RunAsync(state, new ConsoleSink(_env.Out, options.Silent), retryFailed: false, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _env.Error.WriteLine("Cancelled. The session is saved; the app can resume it.");
            return CliExitCode.Cancelled;
        }

        var dependency = finished.Items.Any(item =>
            item.Status == InstallStatus.Failed && (item.Error ?? string.Empty).StartsWith("Not attempted:", StringComparison.Ordinal));
        var failed = finished.Items.Any(item =>
            item.Status is InstallStatus.Failed or InstallStatus.NeedsManualAction or InstallStatus.PostInstallFailed);
        var exit = dependency ? CliExitCode.DependencyFailure : failed ? CliExitCode.PartialFailure : CliExitCode.Success;

        _env.Out.WriteLine();
        foreach (var item in finished.Items)
        {
            var detail = string.IsNullOrEmpty(item.Error) ? item.Detail : item.Error;
            _env.Out.WriteLine($"  [{item.Status}] {item.Name}{(string.IsNullOrEmpty(detail) ? string.Empty : " - " + detail)}");
        }

        _env.Out.WriteLine($"Finished: {finished.CompletedCount} complete, {finished.FailedCount} need attention.");
        WriteJson(options, "install", profile, exit, finished.Items.Select(item =>
            new ReportItem(item.AppId, item.Name, item.Status.ToString(), item.Error ?? item.Detail, NoteFor(profile, item.AppId))), commands);
        return exit;
    }

    private string? NoteFor(AppProfile profile, string appId) =>
        NotesPrivacy.ForExport(profile.Apps.FirstOrDefault(app => app.Id == appId)?.Notes, _env.Settings);

    private void WriteJson(
        CliOptions options, string command, AppProfile profile, int exit,
        IEnumerable<ReportItem> items, IReadOnlyList<CommandPreviewLine> commands)
    {
        if (options.JsonPath is null)
        {
            return;
        }

        var report = new
        {
            tool = "ctrlz",
            command,
            profile = profile.Name,
            exitCode = exit,
            items = items.ToList(),
            commands = commands.Select(line => new
            {
                appId = line.AppId,
                app = line.AppName,
                command = line.CommandText,
                state = line.StateText,
                willRun = line.WillRun
            }).ToList()
        };

        try
        {
            File.WriteAllText(options.JsonPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _env.Error.WriteLine($"Could not write the JSON report: {ex.Message}");
        }
    }

    private sealed record ReportItem(string AppId, string Name, string Status, string? Message, string? Note);

    private sealed class ConsoleSink(TextWriter output, bool silent) : IInstallLogSink
    {
        public void Line(string message)
        {
            if (!silent)
            {
                output.WriteLine("  " + message);
            }
        }

        public void Progress(InstallProgress progress)
        {
        }
    }
}
