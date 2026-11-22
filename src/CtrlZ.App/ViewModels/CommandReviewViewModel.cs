// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CtrlZ.Core.Install;
using CtrlZ.Core.Models;

namespace CtrlZ.App.ViewModels;

/// <summary>One command on the review screen, always shown in full.</summary>
public sealed partial class CommandReviewItem : ObservableObject
{
    public CommandReviewItem(AppEntry app, PostInstallCommand command)
    {
        App = app;
        Command = command;
        Risks = PostInstallCommandRisk.Scan(command);
    }

    public AppEntry App { get; }
    public PostInstallCommand Command { get; }
    public IReadOnlyList<string> Risks { get; }
    public string AppName => App.Name;
    public string CommandText => Command.Describe();
    public bool IsRisky => Risks.Count > 0;
    public string RisksText => IsRisky ? "Risky pattern: " + string.Join(", ", Risks) : string.Empty;
    public string DetailsText =>
        $"Timeout {Command.TimeoutSeconds} s · {(Command.RequiresElevation ? "runs as administrator" : "runs without admin")}" +
        (string.IsNullOrEmpty(Command.WorkingDirectory) ? string.Empty : " · in " + Command.WorkingDirectory);

    [ObservableProperty]
    private bool _isApproved;

    [ObservableProperty]
    private string _stateText = string.Empty;

    internal void Refresh(ProfileOrigin origin, bool approved)
    {
        IsApproved = approved;
        StateText = CommandPreview.ForApp(App, 1, origin, approved)?.StateText ?? string.Empty;
    }
}

/// <summary>
/// Review screen for commands in a profile that did not originate on this PC. Approving writes the
/// command's hash to <see cref="PostInstallApprovalStore"/> only; the profile is never modified.
/// </summary>
public sealed partial class CommandReviewViewModel : ObservableObject
{
    private readonly AppProfile _profile;
    private readonly PostInstallApprovalStore _approvals;
    private readonly Func<string, string, bool> _confirmDanger;
    private readonly Action _markAsLocal;
    private ProfileOrigin _origin;

    /// <param name="confirmDanger">Shows a danger confirmation (title, message); true only when the user agrees.</param>
    /// <param name="markAsLocal">Records the profile as Local. Called only after confirmation.</param>
    public CommandReviewViewModel(
        AppProfile profile,
        ProfileOrigin origin,
        PostInstallApprovalStore approvals,
        Func<string, string, bool> confirmDanger,
        Action markAsLocal)
    {
        _profile = profile;
        _origin = origin;
        _approvals = approvals;
        _confirmDanger = confirmDanger;
        _markAsLocal = markAsLocal;

        Items = profile.Apps
            .Where(app => app.PostInstall is not null)
            .Select(app => new CommandReviewItem(app, app.PostInstall!))
            .ToList();

        RefreshAll();
    }

    public IReadOnlyList<CommandReviewItem> Items { get; }

    public bool HasItems => Items.Count > 0;

    public bool IsLocal => _origin == ProfileOrigin.Local;

    public string HeaderText => IsLocal
        ? "This profile is yours. Its commands run after install without a per-command review."
        : "This profile did not originate on this PC. Its commands stay blocked until you approve each one.";

    public string SubheaderText => IsLocal
        ? "You will still be asked to confirm before commands first run on this PC."
        : "An approval is stored on this PC only, never in the profile file. Editing a command later blocks it again.";

    /// <summary>True once anything was approved or the origin changed, so callers can refresh.</summary>
    public bool Changed { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Current), nameof(PositionText))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(PreviousCommand), nameof(ApproveCommand))]
    private int _index;

    public CommandReviewItem? Current => Index >= 0 && Index < Items.Count ? Items[Index] : null;

    public string PositionText => Items.Count == 0 ? "No commands" : $"{Index + 1} of {Items.Count}";

    public event EventHandler? RequestClose;

    private bool CanNext() => Index < Items.Count - 1;

    [RelayCommand(CanExecute = nameof(CanNext))]
    private void Next() => Index++;

    private bool CanPrevious() => Index > 0;

    [RelayCommand(CanExecute = nameof(CanPrevious))]
    private void Previous() => Index--;

    private bool CanApprove() => !IsLocal && Current is { IsApproved: false };

    [RelayCommand(CanExecute = nameof(CanApprove))]
    private void Approve()
    {
        if (Current is not { } item || IsLocal)
        {
            return;
        }

        if (item.IsRisky && !_confirmDanger(
                "Approve a risky command?",
                $"{item.RisksText}\n\n{item.CommandText}\n\nApprove it only if you understand exactly what it does. " +
                "It will also ask for confirmation every time it is about to run."))
        {
            return;
        }

        _approvals.Approve(_profile.Id, item.Command);
        Changed = true;
        item.Refresh(_origin, _approvals.IsApproved(_profile.Id, item.Command));
        ApproveCommand.NotifyCanExecuteChanged();

        if (CanNext())
        {
            Index++;
        }
    }

    [RelayCommand]
    private void MarkAsLocal()
    {
        if (IsLocal)
        {
            return;
        }

        if (!_confirmDanger(
                "Treat this profile as yours?",
                "Only do this for a profile you created yourself, for example on another of your PCs.\n\n" +
                $"Every command in it ({Items.Count}) will then run after install without a per-command review, " +
                "and you will be able to edit them here."))
        {
            return;
        }

        _markAsLocal();
        _origin = ProfileOrigin.Local;
        Changed = true;
        RefreshAll();
        OnPropertyChanged(nameof(IsLocal));
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(SubheaderText));
        ApproveCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void Close() => RequestClose?.Invoke(this, EventArgs.Empty);

    private void RefreshAll()
    {
        foreach (var item in Items)
        {
            item.Refresh(_origin, _approvals.IsApproved(_profile.Id, item.Command));
        }
    }
}
