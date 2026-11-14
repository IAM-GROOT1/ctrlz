using CommunityToolkit.Mvvm.ComponentModel;
using CtrlZ.Core.Models;

namespace CtrlZ.App.ViewModels;

public sealed partial class ProfileAppRow : ObservableObject
{
    private readonly Action<ProfileAppRow> _changed;

    public ProfileAppRow(AppEntry app, int order, Action<ProfileAppRow> changed)
    {
        App = app;
        Order = order;
        _changed = changed;
        _isIncluded = app.Enabled;
    }

    public AppEntry App { get; }
    public int Order { get; set; }
    public string Name => App.Name;
    public string? Publisher => App.Publisher;
    public string? Version => App.Version;
    public string? IconPath => App.IconPath;
    public string SourceLabel => App.SourceLabel;
    public AppCategory Category => App.Category;
    public string CategoryLabel => App.Category.Display();
    public string InstallSummary => App.InstallSummary;
    public bool NeedsAttention => App.Source is AppSource.Winget or AppSource.MicrosoftStore &&
                                  string.IsNullOrWhiteSpace(App.PackageId);
    public string Meta => string.Join(" · ", new[] { Publisher, Version }
        .Where(value => !string.IsNullOrWhiteSpace(value)));

    [ObservableProperty]
    private bool _isIncluded;

    [ObservableProperty]
    private string _groupName = "Other";

    partial void OnIsIncludedChanged(bool value)
    {
        App.Enabled = value;
        _changed(this);
    }
}
