// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

namespace CtrlZ.Core.Models;

public sealed class AppSettings
{
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public string? ActiveProfileId { get; set; }
    public string? LastBackupFolder { get; set; }
    public string? LastRestoreArchive { get; set; }
    public string? CustomBrowserUserDataPath { get; set; }
    public bool AutoRefreshCatalog { get; set; } = true;
    public double WindowWidth { get; set; } = 1180;
    public double WindowHeight { get; set; } = 760;
    public bool HasCompletedOnboarding { get; set; }

    // ---- Ready-to-wipe checklist ----------------------------------------------------
    /// <summary>Warn when a browser backup is at least this many days old. Zero disables it.</summary>
    public int BackupWarningDays { get; set; } = 14;

    // ---- Installing ------------------------------------------------------------------
    /// <summary>Show the preview as a final confirmation before an install run starts.</summary>
    public bool ShowInstallPreview { get; set; } = true;

    // ---- Updates -------------------------------------------------------------------
    /// <summary>Check GitHub for a newer release in the background at startup.</summary>
    public bool AutoCheckForUpdates { get; set; } = true;

    /// <summary>Stable ignores pre-releases; PreRelease offers whichever is newest.</summary>
    public UpdateChannel UpdateChannel { get; set; } = UpdateChannel.Stable;

    /// <summary>Version the user asked never to be prompted about again.</summary>
    public string? SkippedUpdateVersion { get; set; }

    /// <summary>Last completed check, for the Settings page.</summary>
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }

    /// <summary>"Remind me later" suppresses prompts until this moment.</summary>
    public DateTimeOffset? UpdateRemindAfterUtc { get; set; }
}
