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
    public NavItem(string key, string label, string glyph, PageViewModelBase page, string? badge = null)
    {
        Key = key;
        Label = label;
        Glyph = glyph;
        Page = page;
        Badge = badge;
    }

    public string Key { get; }
    public string Label { get; }
    public string Glyph { get; }
    public PageViewModelBase Page { get; }
    public string? Badge { get; }
    public bool HasBadge => !string.IsNullOrWhiteSpace(Badge);

    [ObservableProperty]
    private bool _isCurrent;
}
