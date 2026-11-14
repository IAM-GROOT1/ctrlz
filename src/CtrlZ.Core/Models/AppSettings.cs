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
}
