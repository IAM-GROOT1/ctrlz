using System.Text.Json.Serialization;

namespace CtrlZ.Core.Models;

public sealed class InstallState
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string ProfileId { get; set; } = string.Empty;
    public string ProfileName { get; set; } = string.Empty;
    public string? ProfilePath { get; set; }
    public DateTimeOffset StartedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public bool IsFinished { get; set; }
    public bool RebootRequested { get; set; }
    public string? LastError { get; set; }
    public string? LogPath { get; set; }

    /// <summary>Frozen copy of the profile so resume survives app/profile edits and reboots.</summary>
    public AppProfile? ProfileSnapshot { get; set; }

    public List<InstallItemState> Items { get; set; } = new();

    [JsonIgnore]
    public int CompletedCount => Items.Count(i => i.Status is InstallStatus.Success or InstallStatus.Skipped or InstallStatus.RebootRequired);

    [JsonIgnore]
    public int FailedCount => Items.Count(i => i.Status is InstallStatus.Failed or InstallStatus.NeedsManualAction);

    [JsonIgnore]
    public bool HasUnfinishedWork => Items.Any(i =>
        i.Status is InstallStatus.Queued or InstallStatus.Preparing or InstallStatus.Downloading or
            InstallStatus.Installing or InstallStatus.Failed or InstallStatus.NeedsManualAction or InstallStatus.Cancelled);
}

public sealed class InstallItemState
{
    public string AppId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public AppSource Source { get; set; }
    public InstallStatus Status { get; set; } = InstallStatus.Queued;
    public int Attempts { get; set; }
    public int? ExitCode { get; set; }
    public string? Error { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record InstallProgress(string AppName, InstallStatus Status, string? Detail = null, double? Fraction = null);

public sealed record InstallResult(bool Success, InstallStatus Status, int? ExitCode = null, string? Error = null, string? Detail = null)
{
    public static InstallResult Ok(string? detail = null) => new(true, InstallStatus.Success, 0, null, detail);
    public static InstallResult Fail(string error, int? exitCode = null) => new(false, InstallStatus.Failed, exitCode, error);
    public static InstallResult NeedsAction(string detail) => new(false, InstallStatus.NeedsManualAction, null, detail, detail);
    public static InstallResult Reboot(string detail) => new(true, InstallStatus.RebootRequired, 3010, null, detail);
}

/// <summary>Runs during install to report line-oriented output and coarse progress.</summary>
public interface IInstallLogSink
{
    void Line(string message);
    void Progress(InstallProgress progress);
}
