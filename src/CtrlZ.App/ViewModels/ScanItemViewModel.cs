// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.App.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CtrlZ.Core.Models;

namespace CtrlZ.App.ViewModels;

public sealed partial class ScanItemViewModel : ObservableObject
{
    private bool _isSelected;

    public ScanItemViewModel(ScannedApp app)
    {
        App = app;
    }

    public ScannedApp App { get; }
    public string Name => App.Name;

    /// <summary>What screen readers announce for this row.</summary>
    public override string ToString() => Name;

    public string? Publisher => App.Publisher;
    public string? Version => App.Version;
    public string? IconPath => App.IconPath;
    public string? PackageId => App.MatchedWingetId ?? App.PackageId;
    public string SourceLabel => App.SourceLabel;
    public bool IsStore => App.IsStoreApp;
    public bool IsMatched => App.IsMatched;
    public bool IsWeak => App.MatchKind == WingetMatchKind.Weak;
    public bool NeedsReview => !IsMatched || IsWeak;

    public string Meta => string.Join(" · ", new[] { App.Publisher, App.Version }
        .Where(value => !string.IsNullOrWhiteSpace(value)));

    public string MatchText => App.MatchKind switch
    {
        WingetMatchKind.Exact => Loc.Get("ScanItem_WingetExact"),
        WingetMatchKind.Strong => Loc.Get("ScanItem_WingetStrong"),
        WingetMatchKind.Weak => Loc.Get("ScanItem_WingetReview"),
        WingetMatchKind.Manual => Loc.Get("ScanItem_WingetManual"),
        _ => Loc.Get("ScanItem_Unmatched")
    };

    public string MatchState => App.MatchKind switch
    {
        WingetMatchKind.Exact or WingetMatchKind.Strong or WingetMatchKind.Manual => "matched",
        WingetMatchKind.Weak => "weak",
        _ => "unmatched"
    };

    public string PackageLabel => App.MatchedWingetId ?? App.PackageId ?? App.MatchReason ?? string.Empty;

    [ObservableProperty]
    private string _groupName = Loc.Get("ScanItem_Other");

    [ObservableProperty]
    private string _sortGroup = "1Other";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public event EventHandler? SelectionChanged;

    public void ApplyMatch(WingetPackage package)
    {
        App.MatchedWingetId = package.Id;
        App.PackageId = package.Id;
        App.MatchKind = WingetMatchKind.Manual;
        App.MatchScore = 100;
        App.MatchReason = Loc.Get("ScanItem_ManuallyMatchedTo") + package.Name;
        RefreshMatch();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RefreshMatch()
    {
        OnPropertyChanged(nameof(IsMatched));
        OnPropertyChanged(nameof(IsWeak));
        OnPropertyChanged(nameof(NeedsReview));
        OnPropertyChanged(nameof(MatchText));
        OnPropertyChanged(nameof(MatchState));
        OnPropertyChanged(nameof(PackageLabel));
    }
}
