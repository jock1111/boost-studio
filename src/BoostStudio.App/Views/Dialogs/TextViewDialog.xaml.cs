using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace BoostStudio.App.Views.Dialogs;

public partial class TextViewDialog : Window
{
    private readonly DispatcherTimer _copyFeedbackTimer;
    private readonly string _copyLabel;

    public TextViewDialog(string title, string message, string content, string? copyLabel = null)
    {
        InitializeComponent();

        Title = string.IsNullOrWhiteSpace(title) ? "Boost Studio" : title;
        TitleText.Text = Title;
        MessageText.Text = message;
        ContentBox.Text = content;

        _copyLabel = string.IsNullOrWhiteSpace(copyLabel) ? "Copy" : copyLabel;
        CopyButton.Content = _copyLabel;
        CopyButton.IsEnabled = !string.IsNullOrEmpty(content);

        _copyFeedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
        _copyFeedbackTimer.Tick += (_, _) =>
        {
            _copyFeedbackTimer.Stop();
            CopyButton.Content = _copyLabel;
        };

        Closed += (_, _) => _copyFeedbackTimer.Stop();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(ContentBox.Text);
            CopyButton.Content = "Copied";
            _copyFeedbackTimer.Stop();
            _copyFeedbackTimer.Start();
        }
        catch (Exception ex) when (ex is COMException or ExternalException or InvalidOperationException)
        {
            CopyButton.Content = "Copy failed";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
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
            DialogResult = true;
        }
    }
}
