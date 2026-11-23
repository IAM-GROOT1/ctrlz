// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1

using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CtrlZ.App.Services;
using CtrlZ.Core.Logging;
using CtrlZ.Core.Tweaks;

namespace CtrlZ.App.ViewModels;

public sealed partial class TweakRowViewModel : ObservableObject
{
    public TweakRowViewModel(TweakDefinition tweak) => Tweak = tweak;

    public TweakDefinition Tweak { get; }
    public string Title => Tweak.Title;
    public string Description => Tweak.Description;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(ActionText), nameof(IsOn))]
    private TweakState _state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private bool _inProfile;

    public bool IsOn => State != TweakState.Off;

    public string ActionText => State == TweakState.Off ? "Apply" : "Undo";

    public string StateText => (State switch
    {
        TweakState.On => "On",
        TweakState.Partly => "Partly on",
        _ => "Off"
    }) + (InProfile ? " · in your profile" : string.Empty);
}

/// <summary>
/// Small, reversible Windows settings. Each one changes only fixed per-user values, saves what was
/// there first, and can be undone exactly. Applied tweaks are recorded in the open profile, so after
/// a reinstall the same list can be applied again in one go.
/// </summary>
public sealed partial class TweaksViewModel : PageViewModelBase
{
    private readonly AppServices _services;
    private readonly DialogService _dialogs;

    public TweaksViewModel(AppServices services, DialogService dialogs)
        : base("Tweaks", "Small Windows settings to set again after a reinstall. Each one can be undone exactly.")
    {
        _services = services;
        _dialogs = dialogs;
    }

    public ObservableCollection<TweakRowViewModel> Rows { get; } = new();

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingFromProfile))]
    private int _pendingFromProfile;

    [ObservableProperty]
    private bool _needsExplorerRestart;

    public bool HasPendingFromProfile => PendingFromProfile > 0;

    public string PendingText => $"Your profile lists {PendingFromProfile} tweak{(PendingFromProfile == 1 ? string.Empty : "s")} that {(PendingFromProfile == 1 ? "is" : "are")} not on this PC yet.";

    public override Task ActivateAsync()
    {
        Refresh();
        return Task.CompletedTask;
    }

    private static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    private void Refresh()
    {
        var chosen = _services.State.ActiveProfile.Tweaks ?? [];
        Rows.Clear();
        foreach (var tweak in TweakCatalog.All.Where(t => !t.Windows11Only || IsWindows11))
        {
            Rows.Add(new TweakRowViewModel(tweak)
            {
                State = SafeState(tweak),
                InProfile = chosen.Contains(tweak.Id, StringComparer.OrdinalIgnoreCase)
            });
        }

        PendingFromProfile = Rows.Count(row => row.InProfile && row.State != TweakState.On);
        OnPropertyChanged(nameof(PendingText));
    }

    private TweakState SafeState(TweakDefinition tweak)
    {
        try
        {
            return _services.Tweaks.StateOf(tweak);
        }
        catch (Exception ex)
        {
            _services.Log.Write(LogLevel.Warning, $"Could not read tweak {tweak.Id}: {LogPrivacy.Describe(ex)}");
            return TweakState.Off;
        }
    }

    [RelayCommand]
    private void Toggle(TweakRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            if (row.State == TweakState.Off)
            {
                _services.Tweaks.Apply(row.Tweak);
                _services.State.SetProfileTweak(row.Tweak.Id, chosen: true);
                StatusMessage = $"{row.Title}: on.";
            }
            else
            {
                _services.Tweaks.Undo(row.Tweak);
                _services.State.SetProfileTweak(row.Tweak.Id, chosen: false);
                StatusMessage = $"{row.Title}: undone.";
            }

            NeedsExplorerRestart |= row.Tweak.NeedsExplorerRestart;
        }
        catch (Exception ex)
        {
            _services.Log.Write(LogLevel.Warning, $"Tweak {row.Tweak.Id} failed: {LogPrivacy.Describe(ex)}");
            _dialogs.Error(ex.Message, "Tweak not changed");
        }

        Refresh();
    }

    /// <summary>After a reinstall: applies every tweak the profile lists that is not on yet.</summary>
    [RelayCommand]
    private void ApplyFromProfile()
    {
        var pending = Rows.Where(row => row.InProfile && row.State != TweakState.On).ToList();
        if (pending.Count == 0 ||
            !_dialogs.Confirm("Apply tweaks from your profile",
                "These will be set on this PC, and each can be undone here:\n\n" +
                string.Join("\n", pending.Select(row => "• " + row.Title)),
                "Apply", "Cancel"))
        {
            return;
        }

        var failed = new List<string>();
        foreach (var row in pending)
        {
            try
            {
                _services.Tweaks.Apply(row.Tweak);
                NeedsExplorerRestart |= row.Tweak.NeedsExplorerRestart;
            }
            catch (Exception ex)
            {
                _services.Log.Write(LogLevel.Warning, $"Tweak {row.Tweak.Id} failed: {LogPrivacy.Describe(ex)}");
                failed.Add(row.Title);
            }
        }

        StatusMessage = failed.Count == 0
            ? $"Applied {pending.Count} tweak{(pending.Count == 1 ? string.Empty : "s")} from your profile."
            : $"Applied {pending.Count - failed.Count}; not changed: {string.Join(", ", failed)}.";
        Refresh();
    }

    /// <summary>Some tweaks take effect only after File Explorer restarts. Asks first: open windows close.</summary>
    [RelayCommand]
    private async Task RestartExplorerAsync()
    {
        if (!_dialogs.Confirm("Restart File Explorer",
                "File Explorer and the taskbar will close and open again. Open Explorer windows will close; nothing else is affected.",
                "Restart Explorer", "Cancel"))
        {
            return;
        }

        try
        {
            StatusMessage = "Restarting File Explorer…";

            // Waiting for Explorer to close and come back runs off the UI thread, so the window stays responsive.
            await Task.Run(async () =>
            {
                int session;
                using (var self = Process.GetCurrentProcess())
                {
                    session = self.SessionId;
                }

                foreach (var explorer in Process.GetProcessesByName("explorer"))
                {
                    using (explorer)
                    {
                        if (explorer.SessionId == session)
                        {
                            explorer.Kill();
                            explorer.WaitForExit(5000);
                        }
                    }
                }

                // Windows usually restarts the shell on its own; start it if it did not.
                await Task.Delay(1500).ConfigureAwait(false);
                if (!ExplorerRunningIn(session))
                {
                    using var started = Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
                }
            });

            NeedsExplorerRestart = false;
            StatusMessage = "File Explorer restarted.";
        }
        catch (Exception ex)
        {
            _services.Log.Write(LogLevel.Warning, $"Explorer restart failed: {LogPrivacy.Describe(ex)}");
            StatusMessage = null;
            _dialogs.Error("File Explorer could not be restarted. Sign out and back in to apply the change.\n\n" + ex.Message);
        }
    }

    private static bool ExplorerRunningIn(int session)
    {
        var running = false;
        foreach (var explorer in Process.GetProcessesByName("explorer"))
        {
            using (explorer)
            {
                running |= explorer.SessionId == session;
            }
        }

        return running;
    }
}
