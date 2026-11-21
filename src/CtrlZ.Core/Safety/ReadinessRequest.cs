// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

namespace CtrlZ.Core.Safety;

/// <summary>A user folder to size up, with the label shown in the UI.</summary>
/// <param name="Options">Per-folder limits; falls back to the request's limits when null.</param>
public sealed record UserFolder(string Label, string Path, FolderUsageOptions? Options = null);

/// <summary>A browser the app detected, reduced to what the report needs.</summary>
public sealed record DetectedBrowserSummary(string Name, int ProfileCount);

/// <summary>Drift counts for the profile row; filled in by the drift analysis when available.</summary>
public sealed record DriftSummary(int Missing, int Extra, int Outdated, int Ignored);

/// <summary>Progress of one readiness scan.</summary>
public sealed record ReadinessProgress(string Step, int Index, int Total);

/// <summary>Everything a readiness scan needs. Tests build one pointing at a temp tree.</summary>
public sealed record ReadinessRequest
{
    public SafetyEnvironment Environment { get; init; } = SafetyEnvironment.Detect();

    /// <summary>Folder CTRL+Z writes its browser backups to, if the user configured one.</summary>
    public string? BackupFolder { get; init; }

    /// <summary>Warn when a browser backup is at least this many days old. Zero disables it.</summary>
    public int BackupWarningDays { get; init; } = 14;

    public IReadOnlyList<UserFolder> UserFolders { get; init; } = [];

    public IReadOnlyList<DetectedBrowserSummary> Browsers { get; init; } = [];

    public DateTimeOffset? ProfileUpdatedUtc { get; init; }

    public int? ProfileAppCount { get; init; }

    public DriftSummary? Drift { get; init; }

    /// <summary>Folders at or above this size count as "large" and are flagged. Zero disables it.</summary>
    public long LargeFolderBytes { get; init; } = 1024L * 1024 * 1024;

    /// <summary>Content changed within this many days counts as "recent". Zero disables it.</summary>
    public int RecentDays { get; init; } = 7;

    public FolderUsageOptions FolderOptions { get; init; } = FolderUsageOptions.Default;

    /// <summary>Clock override, so tests can pin "now".</summary>
    public DateTimeOffset? Now { get; init; }
}

/// <summary>The hand-hold reminders the user ticks themselves. CTRL+Z cannot check these.</summary>
public static class ReadinessReminders
{
    public const string IdPrefix = "reminder.";

    public sealed record Reminder(string Id, string Label, string WhatItMeans, string SuggestedAction);

    public static readonly IReadOnlyList<Reminder> All =
    [
        new("export-password-manager", "Export your password manager",
            "A clean install removes browser password stores and any local vault.",
            "Export an encrypted copy to external media you will still have after the wipe."),
        new("license-keys", "Note your licence keys",
            "Some paid applications need the original key to activate again.",
            "Keep the keys in your password manager or another machine."),
        new("deactivate-software", "Deactivate licensed software",
            "Many licences allow only one machine at a time and will refuse to activate after a wipe.",
            "Deactivate inside each application before you wipe."),
        new("sign-out", "Sign out of services",
            "Old devices left signed in can lock you out or leave stale sessions.",
            "Sign out of the accounts you will re-add later."),
        new("wifi-passwords", "Save your Wi-Fi passwords",
            "Windows may not remember your networks after a reinstall.",
            "Write them down or store them in your password manager."),
        new("recovery-keys", "Check your recovery keys",
            "BitLocker and account recovery keys stored only on this PC are lost when it is wiped.",
            "Confirm you can reach those keys from another device.")
    ];
}
