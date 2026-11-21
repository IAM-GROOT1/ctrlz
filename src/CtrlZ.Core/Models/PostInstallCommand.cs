// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

namespace CtrlZ.Core.Models;

/// <summary>
/// An optional command that runs after an app installs. Never silent: it is printed in the preview,
/// needs confirmation on first use per machine, and is disabled by default for profiles that did not
/// originate on this machine.
/// </summary>
public sealed class PostInstallCommand
{
    /// <summary>The command line. Runs with the app's own privileges unless <see cref="RequiresElevation"/>.</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>Stored preference. Ignored for imported profiles: those start disabled regardless.</summary>
    public bool Enabled { get; set; }

    /// <summary>Opt-in elevation for this one command. Off by default.</summary>
    public bool RequiresElevation { get; set; }

    /// <summary>Timeout in seconds. Clamped to a sane range when the command runs.</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Off by default: only the exit code and duration are logged, never the output.</summary>
    public bool LogOutput { get; set; }

    public PostInstallCommand Clone() => new()
    {
        Command = Command,
        Enabled = Enabled,
        RequiresElevation = RequiresElevation,
        TimeoutSeconds = TimeoutSeconds,
        LogOutput = LogOutput
    };
}
