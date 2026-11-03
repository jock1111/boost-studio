using System.Windows;
using System.Windows.Input;

namespace BoostStudio.App.Views.Dialogs;

public partial class PromptDialog : Window
{
    public PromptDialog(string title, string message, string label, string initialValue = "",
        string? help = null, int maxLength = 80)
    {
        InitializeComponent();

        Title = string.IsNullOrWhiteSpace(title) ? "Boost Studio" : title;
        TitleText.Text = Title;
        MessageText.Text = message;
        FieldLabel.Text = label;
        ValueBox.Text = initialValue;
        ValueBox.MaxLength = maxLength > 0 ? maxLength : 80;

        if (!string.IsNullOrWhiteSpace(help))
        {
            HelpText.Text = help;
            HelpText.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    public string Value { get; private set; } = string.Empty;

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var value = ValueBox.Text.Trim();
        if (value.Length == 0)
        {
            ErrorText.Text = "This field cannot be empty.";
            ErrorText.Visibility = Visibility.Visible;
            ValueBox.Focus();
            return;
        }

        Value = value;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The mouse button can be released between the event and DragMove.
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DialogResult = false;
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Confirm_Click(this, new RoutedEventArgs());
        }
    }
}
