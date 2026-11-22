// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

namespace CtrlZ.Core.Models;

/// <summary>
/// An optional command that runs after an app installs. Stored as an executable plus an argument
/// list, never as one shell string, so it cannot be interpreted by a shell by accident. Never silent:
/// it is printed in the preview and gated by <see cref="CtrlZ.Core.Install.PostInstallCommandGate"/>.
/// </summary>
public sealed class PostInstallCommand
{
    /// <summary>Executable to start. No shell is involved.</summary>
    public string Executable { get; set; } = string.Empty;

    /// <summary>Arguments, one list item each. Never concatenated into a command line.</summary>
    public List<string> Arguments { get; set; } = new();

    /// <summary>Working directory, or null for the default.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Stored preference. Ignored for profiles that did not originate on this machine.</summary>
    public bool Enabled { get; set; }

    /// <summary>Opt-in elevation for this one command. Off by default.</summary>
    public bool RequiresElevation { get; set; }

    /// <summary>Timeout in seconds. Clamped to 5..600 when it runs.</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Full text for the preview and the review screen. Nothing is hidden from the user.</summary>
    public string Describe()
    {
        var parts = new List<string> { Executable };
        parts.AddRange(Arguments);
        var text = string.Join(' ', parts);
        return string.IsNullOrWhiteSpace(WorkingDirectory) ? text : $"{text}  (in {WorkingDirectory})";
    }

    public PostInstallCommand Clone() => new()
    {
        Executable = Executable,
        Arguments = new List<string>(Arguments),
        WorkingDirectory = WorkingDirectory,
        Enabled = Enabled,
        RequiresElevation = RequiresElevation,
        TimeoutSeconds = TimeoutSeconds
    };
}
