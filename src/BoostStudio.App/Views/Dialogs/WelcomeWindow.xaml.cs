using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BoostStudio.App.Services;
using BoostStudio.Core.Models;

namespace BoostStudio.App.Views.Dialogs;

public partial class WelcomeWindow : Window
{
    public WelcomeWindow(EngineStatus status)
    {
        InitializeComponent();

        if (status.Installed)
        {
            StatusDot.Fill = (Brush)FindResource("SuccessBrush");
            StatusTitle.Text = "Equalizer APO detected";
            StatusDetail.Text = string.IsNullOrWhiteSpace(status.Version)
                ? "The audio engine is installed. Connect a module when you are ready."
                : $"Version {status.Version}. Connect a module when you are ready.";
            StatusBadge.Text = "ENGINE FOUND";
            StatusBadge.Foreground = (Brush)FindResource("SuccessBrush");
        }
        else
        {
            StatusDot.Fill = (Brush)FindResource("WarningBrush");
            StatusTitle.Text = "Equalizer APO is not installed";
            StatusDetail.Text = "Boost Studio needs this free engine for system-wide processing. " +
                                "Setup instructions are below.";
            StatusBadge.Text = "SETUP NEEDED";
            StatusBadge.Foreground = (Brush)FindResource("WarningBrush");
        }

        Loaded += (_, _) => Keyboard.Focus(this);
    }

    public WelcomeChoice Choice { get; private set; } = WelcomeChoice.Close;

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        Choice = WelcomeChoice.Download;
        Close();
    }

    private void Recheck_Click(object sender, RoutedEventArgs e)
    {
        Choice = WelcomeChoice.Recheck;
        Close();
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        Choice = WelcomeChoice.Close;
        Close();
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
            Choice = WelcomeChoice.Close;
            Close();
        }
    }
}
