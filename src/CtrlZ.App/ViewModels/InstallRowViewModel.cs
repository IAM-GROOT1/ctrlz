// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.App.Localization;
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
    private string _detail = Loc.Get("InstallRow_Queued");

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private int _attempts;

    public string StatusText => Status switch
    {
        InstallStatus.Queued => Loc.Get("InstallRow_Queued"),
        InstallStatus.Preparing => Loc.Get("InstallRow_Preparing"),
        InstallStatus.Downloading => Loc.Get("InstallRow_Downloading"),
        InstallStatus.Installing => Loc.Get("InstallRow_Installing"),
        InstallStatus.Success => Loc.Get("InstallRow_Installed"),
        InstallStatus.Failed => Loc.Get("InstallRow_Failed"),
        InstallStatus.NeedsManualAction => Loc.Get("InstallRow_ManualAction"),
        InstallStatus.RebootRequired => Loc.Get("InstallRow_RestartNeeded"),
        InstallStatus.Skipped => Loc.Get("InstallRow_Skipped"),
        InstallStatus.Cancelled => Loc.Get("InstallRow_Cancelled"),
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

    public string NoteText => ShowsNote ? Loc.Get("InstallRow_YourNote") + Note : string.Empty;

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
