using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using BoostStudio.App.ViewModels;

namespace BoostStudio.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _allowClose;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        _viewModel.ShowWindowRequested += OnShowWindowRequested;
        _viewModel.ExitRequested += OnExitRequested;
        SourceInitialized += OnSourceInitialized;
        StateChanged += OnWindowStateChanged;
        Closing += OnWindowClosing;
    }

    public void ShowFromTray()
    {
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        ShowInTaskbar = true;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
        _ = _viewModel.MaybeShowWelcomeAsync();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _viewModel.AttachWindow(handle);
        ApplyWindowAppearance(handle);
    }

    private void OnShowWindowRequested(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(ShowFromTray, DispatcherPriority.Normal);
            return;
        }

        ShowFromTray();
    }

    private void OnExitRequested(object? sender, EventArgs e)
    {
        _allowClose = true;
        Close();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
            return;

        if (!_viewModel.Settings.CloseToTray)
        {
            _allowClose = true;
            return;
        }

        e.Cancel = true;
        Hide();
        _viewModel.NotifyMinimizedToTray();
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            MaximizeButton.Content = "\uE923";
            MaximizeButton.ToolTip = "Restore";
        }
        else
        {
            MaximizeButton.Content = "\uE922";
            MaximizeButton.ToolTip = "Maximize";
        }

        if (WindowState != WindowState.Minimized || !_viewModel.Settings.MinimizeToTray)
            return;

        // Let Windows complete the minimize transition before hiding the window so
        // the taskbar animation does not visibly snap.
        Dispatcher.BeginInvoke(() =>
        {
            if (!_viewModel.Settings.MinimizeToTray) return;
            Hide();
            _viewModel.NotifyMinimizedToTray();
        }, DispatcherPriority.Background);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (FindButtonAncestor(e.OriginalSource as DependencyObject) is not null) return;

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (WindowState == WindowState.Maximized)
            WindowState = WindowState.Normal;

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The mouse button was released before the drag loop started.
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private static ButtonBase? FindButtonAncestor(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase button)
                return button;
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private static void ApplyWindowAppearance(IntPtr handle)
    {
        try
        {
            // Immersive dark title bar and rounded corners on Windows 11. Both calls
            // are best-effort and simply do nothing on older builds.
            var dark = 1;
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
            var corners = 2;
            DwmSetWindowAttribute(handle, 33, ref corners, sizeof(int));
        }
        catch
        {
            // Appearance is not critical.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
