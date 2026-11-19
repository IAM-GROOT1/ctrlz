// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CommunityToolkit.Mvvm.ComponentModel;

namespace CtrlZ.App.ViewModels;

public abstract partial class PageViewModelBase : ObservableObject
{
    protected PageViewModelBase(string title, string subtitle)
    {
        Title = title;
        Subtitle = subtitle;
    }

    public string Title { get; protected set; }
    public string Subtitle { get; protected set; }
    public virtual Task ActivateAsync() => Task.CompletedTask;
}

public sealed partial class NavItem : ObservableObject
{
    public NavItem(string key, string label, string glyph, PageViewModelBase page)
    {
        Key = key;
        Label = label;
        Glyph = glyph;
        Page = page;
    }

    public string Key { get; }
    public string Label { get; }
    public string Glyph { get; }
    public PageViewModelBase Page { get; }

    [ObservableProperty]
    private bool _isCurrent;
}
