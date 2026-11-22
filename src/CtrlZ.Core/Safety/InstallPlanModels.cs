// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.Core.Models;

namespace CtrlZ.Core.Safety;

/// <summary>How a single planned app looks before anything runs.</summary>
public enum PlanCheckStatus
{
    /// <summary>Nothing looks wrong.</summary>
    Ready,

    /// <summary>It can run, but something is worth knowing first.</summary>
    Warning,

    /// <summary>The dry run proved this app cannot be installed as configured.</summary>
    Failed,

    /// <summary>Not checked (for example a store app with nothing to verify).</summary>
    Skipped
}

/// <summary>One app in an install preview.</summary>
public sealed record InstallPlanItem
{
    public required string AppId { get; init; }
    public required string Name { get; init; }
    public AppSource Source { get; init; }
    public string SourceDescription { get; init; } = string.Empty;
    public string? PackageId { get; init; }
    public string? Version { get; init; }
    public string SilentArgs { get; init; } = string.Empty;
    public bool RequiresAdmin { get; init; }

    /// <summary>1-based position in the run.</summary>
    public int Order { get; init; }

    /// <summary>Neutral facts about the app, such as where it comes from.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Things the user should know before running: missing file, no hash, unsigned.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>Everything the install button will do, described before it does it.</summary>
public sealed record InstallPlan
{
    public required string ProfileName { get; init; }
    public IReadOnlyList<InstallPlanItem> Items { get; init; } = [];

    /// <summary>Plan-wide notes, for example that order is literal and dependencies are not resolved.</summary>
    public IReadOnlyList<string> PlanWarnings { get; init; } = [];

    public int Count => Items.Count;

    public int WarningCount => Items.Count(item => item.Warnings.Count > 0);

    public int AdminCount => Items.Count(item => item.RequiresAdmin);

    public bool IsEmpty => Items.Count == 0;
}

/// <summary>Result for one app after a dry run.</summary>
public sealed record DryRunItem
{
    public required string AppId { get; init; }
    public required string Name { get; init; }
    public AppSource Source { get; init; }
    public required PlanCheckStatus Status { get; init; }
    public required string Message { get; init; }
    public long? DownloadBytes { get; init; }
}

/// <summary>Whole dry-run outcome, exportable as text.</summary>
public sealed record DryRunReport
{
    public DateTimeOffset GeneratedUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ProfileName { get; init; } = string.Empty;
    public IReadOnlyList<DryRunItem> Items { get; init; } = [];

    /// <summary>Every post-install command with its state. Listed only; the dry run never runs one.</summary>
    public IReadOnlyList<Install.CommandPreviewLine> Commands { get; init; } = [];

    public int ReadyCount => Items.Count(item => item.Status == PlanCheckStatus.Ready);

    public int WarningCount => Items.Count(item => item.Status == PlanCheckStatus.Warning);

    public int FailedCount => Items.Count(item => item.Status == PlanCheckStatus.Failed);

    public long KnownBytes => Items.Sum(item => item.DownloadBytes ?? 0);

    public bool HasFailures => FailedCount > 0;

    /// <summary>Plain-text report for the export button.</summary>
    public string ToText()
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("CTRL+Z dry run");
        builder.AppendLine($"Profile: {ProfileName}");
        builder.AppendLine($"Generated: {GeneratedUtc.ToLocalTime():yyyy-MM-dd HH:mm}");
        builder.AppendLine();

        foreach (var item in Items)
        {
            var mark = item.Status switch
            {
                PlanCheckStatus.Ready => "OK  ",
                PlanCheckStatus.Warning => "WARN",
                PlanCheckStatus.Failed => "FAIL",
                _ => "SKIP"
            };

            builder.AppendLine($"[{mark}] {item.Name} ({item.Source.Display()})");
            builder.AppendLine($"       {item.Message}");
            if (item.DownloadBytes is > 0)
            {
                builder.AppendLine($"       size: {Infrastructure.FileUtil.FormatBytes(item.DownloadBytes.Value)}");
            }
        }

        builder.AppendLine();
        builder.AppendLine($"Summary: {ReadyCount} ready, {WarningCount} warnings, {FailedCount} failed");
        if (KnownBytes > 0)
        {
            builder.AppendLine($"Known download size: {Infrastructure.FileUtil.FormatBytes(KnownBytes)}");
        }

        if (Commands.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Post-install commands");
            foreach (var command in Commands)
            {
                builder.AppendLine($"  {command.Order}. {command.AppName}: {command.CommandText}");
                builder.AppendLine($"       {command.StateText}");
            }

            builder.AppendLine("No post-install command was run by this check.");
        }

        builder.AppendLine("Nothing was installed, downloaded to disk, or changed by this check.");
        return builder.ToString();
    }
}
