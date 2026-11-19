using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using CtrlZ.Core.Updates;

namespace CtrlZ.App.Views;

public enum UpdatePromptOutcome
{
    UpdateNow,
    RemindLater,
    SkipVersion,
    Dismissed
}

/// <summary>
/// The single window used for both the update prompt and download progress. It is shown
/// with <see cref="Show"/> rather than <c>ShowDialog</c> so the coordinator can keep awaiting
/// the download while the window stays responsive.
/// </summary>
public partial class UpdatePromptWindow : Window
{
    private TaskCompletionSource<UpdatePromptOutcome>? _completion;
    private bool _closing;

    public UpdatePromptWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
    }

    /// <summary>Raised when the user asks to cancel an in-flight download.</summary>
    public event EventHandler? CancelRequested;

    public string ReleaseUrl { get; private set; } = string.Empty;

    public void Configure(string currentVersion, ReleaseInfo release, string notes)
    {
        ReleaseUrl = release.HtmlUrl;
        HeadlineText.Text = release.IsPreRelease
            ? $"CTRL+Z {release.Version} (pre-release) is available"
            : $"CTRL+Z {release.Version} is available";
        SubText.Text = $"You are running {currentVersion}.";
        NotesText.Text = string.IsNullOrWhiteSpace(notes) ? "No release notes were provided." : notes;
    }

    public Task<UpdatePromptOutcome> PromptAsync(Window? owner = null)
    {
        _completion = new TaskCompletionSource<UpdatePromptOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (owner is not null && owner.IsVisible && !ReferenceEquals(owner, this))
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        Loaded += (_, _) => UpdateNowButton.Focus();
        Closed += (_, _) => _completion?.TrySetResult(UpdatePromptOutcome.Dismissed);

        Show();
        return _completion.Task;
    }

    /// <summary>Switches to the download state: actions are replaced by a single Cancel.</summary>
    public void BeginProgress(string status)
    {
        _closing = false;
        SkipButton.Visibility = Visibility.Collapsed;
        RemindButton.Visibility = Visibility.Collapsed;
        UpdateNowButton.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Visible;
        CloseButton.Visibility = Visibility.Collapsed;
        ReleaseLinkButton.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        ErrorText.Visibility = Visibility.Collapsed;
        Progress.IsIndeterminate = true;
        ProgressText.Text = status;
        CancelButton.Focus();
    }

    public void SetProgress(double? fraction, string status)
    {
        ProgressText.Text = status;
        if (fraction is null)
        {
            Progress.IsIndeterminate = true;
            return;
        }

        Progress.IsIndeterminate = false;
        Progress.Value = Math.Clamp(fraction.Value, 0, 1);
    }

    /// <summary>Terminal failure: shows the reason and offers only Close.</summary>
    public void ShowError(string message)
    {
        ProgressPanel.Visibility = Visibility.Collapsed;
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        _closing = true;
        CancelButton.Visibility = Visibility.Visible;
        CancelButton.Content = "Close";
        CancelButton.IsEnabled = true;
    }

    public void CloseFromCoordinator()
    {
        _closing = true;
        Close();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        if (CancelButton.Visibility == Visibility.Visible)
        {
            CancelRequested?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            Complete(UpdatePromptOutcome.Dismissed);
        }
    }

    private void Complete(UpdatePromptOutcome outcome)
    {
        if (_completion is null || _completion.Task.IsCompleted)
        {
            return;
        }

        _completion.TrySetResult(outcome);
    }

    private void UpdateNow_Click(object sender, RoutedEventArgs e) => Complete(UpdatePromptOutcome.UpdateNow);

    private void Remind_Click(object sender, RoutedEventArgs e) => Complete(UpdatePromptOutcome.RemindLater);

    private void Skip_Click(object sender, RoutedEventArgs e) => Complete(UpdatePromptOutcome.SkipVersion);

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_closing)
        {
            CloseFromCoordinator();
            return;
        }

        CancelButton.IsEnabled = false;
        ProgressText.Text = "Cancelling\u2026";
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Complete(UpdatePromptOutcome.Dismissed);

    private void ReleaseLink_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ReleaseUrl))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(ReleaseUrl) { UseShellExecute = true });
        }
        catch
        {
            // Opening the browser is optional; never block the prompt on it.
        }
    }
}
