using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BoostStudio.App.Services;

namespace BoostStudio.App.Views.Dialogs;

public partial class MessageDialog : Window
{
    private static readonly Color InfoColor = Color.FromRgb(0x35, 0xE0, 0xC3);
    private static readonly Color SuccessColor = Color.FromRgb(0x4A, 0xDE, 0x80);
    private static readonly Color WarningColor = Color.FromRgb(0xF5, 0xC9, 0x5D);
    private static readonly Color ErrorColor = Color.FromRgb(0xF9, 0x70, 0x66);
    private static readonly Color QuestionColor = Color.FromRgb(0x8B, 0x5C, 0xF6);

    public MessageDialog(string title, string message, string? detail, DialogKind kind,
        string? confirmText = null, string? cancelText = null, bool danger = false)
    {
        InitializeComponent();

        Title = string.IsNullOrWhiteSpace(title) ? "Boost Studio" : title;
        TitleText.Text = Title;
        MessageText.Text = message;

        if (!string.IsNullOrWhiteSpace(detail))
        {
            DetailText.Text = detail;
            DetailBorder.Visibility = Visibility.Visible;
        }

        var (glyph, color) = kind switch
        {
            DialogKind.Success => ("\uE930", SuccessColor),
            DialogKind.Warning => ("\uE7BA", WarningColor),
            DialogKind.Error => ("\uEA39", ErrorColor),
            DialogKind.Question => ("\uE897", QuestionColor),
            _ => ("\uE946", InfoColor)
        };

        IconGlyph.Text = glyph;
        IconGlyph.Foreground = new SolidColorBrush(color);
        IconBorder.Background = new SolidColorBrush(Color.FromArgb(0x22, color.R, color.G, color.B));
        IconBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, color.R, color.G, color.B));
        AccentStop.Color = color;

        var isQuestion = kind == DialogKind.Question;
        CancelButton.Content = cancelText ?? "Cancel";
        CancelButton.Visibility = isQuestion ? Visibility.Visible : Visibility.Collapsed;
        ConfirmButton.Content = confirmText ?? (isQuestion ? "Continue" : "OK");
        ConfirmButton.Style = (Style)FindResource(danger ? "DangerButton" : "PrimaryButton");

        Loaded += (_, _) =>
        {
            ConfirmButton.Focus();
            Keyboard.Focus(isQuestion ? CancelButton : ConfirmButton);
        };
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // The mouse button can be released between the event and DragMove.
            }
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
    }
}
