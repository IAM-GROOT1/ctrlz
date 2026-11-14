using System.Windows;
using System.Windows.Input;

namespace CtrlZ.App.Views;

public enum DialogOutcome
{
    Primary,
    Secondary,
    Cancel
}

public sealed class DialogResult
{
    public DialogOutcome Outcome { get; init; } = DialogOutcome.Cancel;
    public string? InputText { get; init; }
    public bool IsChecked { get; init; }
}

public partial class DialogWindow : Window
{
    public DialogWindow()
    {
        InitializeComponent();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Result = new DialogResult { Outcome = DialogOutcome.Cancel, InputText = InputBox.Text, IsChecked = ExtraCheckBox.IsChecked == true };
                Close();
            }
        };
        Loaded += (_, _) =>
        {
            if (InputPanel.Visibility == Visibility.Visible)
            {
                InputBox.Focus();
                InputBox.SelectAll();
            }
            else
            {
                PrimaryButton.Focus();
            }
        };
    }

    public DialogResult Result { get; private set; } = new();

    public void Configure(
        string title,
        string message,
        string primary,
        string? secondary,
        string? inputLabel,
        string? initialInput,
        string? checkText,
        bool danger)
    {
        TitleText.Text = title;
        MessageText.Text = message;
        PrimaryButton.Content = primary;
        PrimaryButton.Style = (Style)FindResource(danger ? "Button.Danger" : "Button.Primary");
        if (string.IsNullOrWhiteSpace(secondary))
        {
            SecondaryButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            SecondaryButton.Content = secondary;
        }

        if (inputLabel is not null)
        {
            InputPanel.Visibility = Visibility.Visible;
            InputLabel.Text = inputLabel;
            InputBox.Text = initialInput ?? string.Empty;
        }

        if (checkText is not null)
        {
            ExtraCheckBox.Visibility = Visibility.Visible;
            ExtraCheckBox.Content = checkText;
        }
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        Result = new DialogResult
        {
            Outcome = DialogOutcome.Primary,
            InputText = InputBox.Text,
            IsChecked = ExtraCheckBox.IsChecked == true
        };
        Close();
    }

    private void Secondary_Click(object sender, RoutedEventArgs e)
    {
        Result = new DialogResult
        {
            Outcome = DialogOutcome.Secondary,
            InputText = InputBox.Text,
            IsChecked = ExtraCheckBox.IsChecked == true
        };
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Result = new DialogResult
        {
            Outcome = DialogOutcome.Cancel,
            InputText = InputBox.Text,
            IsChecked = ExtraCheckBox.IsChecked == true
        };
        Close();
    }
}
