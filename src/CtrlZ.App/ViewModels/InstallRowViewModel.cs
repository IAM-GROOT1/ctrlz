// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CommunityToolkit.Mvvm.ComponentModel;
using CtrlZ.Core.Models;

namespace CtrlZ.App.ViewModels;

public sealed partial class InstallRowViewModel : ObservableObject
{
    public InstallRowViewModel(string name, AppSource source, string? iconPath)
    {
        Name = name;
        Source = source;
        IconPath = iconPath;
    }

    public string Name { get; }
    public AppSource Source { get; }
    public string SourceLabel => Source.Display();
    public string? IconPath { get; }
    public string FallbackText => Name;

    [ObservableProperty]
    private InstallStatus _status = InstallStatus.Queued;

    [ObservableProperty]
    private string _detail = "Queued";

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private int _attempts;

    public string StatusText => Status switch
    {
        InstallStatus.Queued => "Queued",
        InstallStatus.Preparing => "Preparing",
        InstallStatus.Downloading => "Downloading",
        InstallStatus.Installing => "Installing",
        InstallStatus.Success => "Installed",
        InstallStatus.Failed => "Failed",
        InstallStatus.NeedsManualAction => "Manual action",
        InstallStatus.RebootRequired => "Restart needed",
        InstallStatus.Skipped => "Skipped",
        InstallStatus.Cancelled => "Cancelled",
        _ => Status.ToString()
    };

    public string Tone => Status switch
    {
        InstallStatus.Success => "success",
        InstallStatus.Failed => "danger",
        InstallStatus.NeedsManualAction => "warning",
        InstallStatus.RebootRequired => "warning",
        InstallStatus.Downloading or InstallStatus.Installing or InstallStatus.Preparing => "active",
        InstallStatus.Skipped or InstallStatus.Cancelled => "muted",
        _ => "queued"
    };

    public bool ShowsProgress => Status is InstallStatus.Downloading or InstallStatus.Installing or InstallStatus.Preparing;
    public bool IsDone => Status is InstallStatus.Success or InstallStatus.Skipped or InstallStatus.RebootRequired;

    /// <summary>The app's own note. Shown on this screen only, never written to the log.</summary>
    public string? Note { get; init; }

    /// <summary>The note matters when the user has to act, so it appears for failed and manual apps.</summary>
    public bool ShowsNote => !string.IsNullOrWhiteSpace(Note) &&
                             Status is InstallStatus.Failed or InstallStatus.NeedsManualAction;

    public string NoteText => ShowsNote ? "Your note: " + Note : string.Empty;

    partial void OnStatusChanged(InstallStatus value)
    {
        OnPropertyChanged(nameof(ShowsNote));
        OnPropertyChanged(nameof(NoteText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(Tone));
        OnPropertyChanged(nameof(ShowsProgress));
        OnPropertyChanged(nameof(IsDone));
    }

    public void Update(InstallStatus status, string? detail, double? fraction = null)
    {
        Status = status;
        if (!string.IsNullOrWhiteSpace(detail))
        {
            Detail = detail;
        }

        if (fraction is not null)
        {
            Progress = Math.Clamp(fraction.Value, 0, 1);
        }
        else if (status is InstallStatus.Success or InstallStatus.RebootRequired or InstallStatus.Skipped)
        {
            Progress = 1;
        }
    }
}
