// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CtrlZ.App.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CtrlZ.Core.Safety;

namespace CtrlZ.App.ViewModels;

/// <summary>One row of the ready-to-wipe checklist, with its handled/ignored state.</summary>
public sealed partial class ReadinessRow : ObservableObject
{
    public ReadinessRow(ReadinessCheck check, bool handled, bool ignored)
    {
        Id = check.Id;
        Title = check.Title;
        Status = check.Status;
        Summary = check.Summary;
        WhatItMeans = check.WhatItMeans;
        SuggestedAction = check.SuggestedAction;
        Details = check.Details;
        LinksToDrift = check.LinksToDrift;
        _isHandled = handled;
        _isIgnored = ignored;
    }

    public string Id { get; }
    public string Title { get; }
    public ReadinessStatus Status { get; }
    public string Summary { get; }
    public string WhatItMeans { get; }
    public string SuggestedAction { get; }
    public IReadOnlyList<string> Details { get; }
    public bool LinksToDrift { get; }

    public bool HasDetails => Details.Count > 0;
    public bool HasSuggestedAction => !string.IsNullOrWhiteSpace(SuggestedAction);
    public bool IsResolved => IsHandled || IsIgnored;

    public string StatusGlyph => Status switch
    {
        ReadinessStatus.Fail => "\uEA39",   // error badge
        ReadinessStatus.Warning => "\uE7BA", // warning
        _ => "\uE73E"                        // check
    };

    public string StatusLabel => Status switch
    {
        ReadinessStatus.Fail => Loc.Get("ReadinessRow_ActionNeeded"),
        ReadinessStatus.Warning => Loc.Get("ReadinessRow_CheckThis"),
        _ => Loc.Get("ReadinessRow_LooksFine")
    };

    [ObservableProperty]
    private bool _isHandled;

    [ObservableProperty]
    private bool _isIgnored;

    [ObservableProperty]
    private bool _isExpanded;

    partial void OnIsHandledChanged(bool value) => OnPropertyChanged(nameof(IsResolved));

    partial void OnIsIgnoredChanged(bool value) => OnPropertyChanged(nameof(IsResolved));
}
