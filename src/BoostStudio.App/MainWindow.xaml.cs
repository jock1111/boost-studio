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
    private readonly DispatcherTimer _placementTimer;
    private bool _allowClose;
    private bool _restoringPlacement;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        _placementTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _placementTimer.Tick += OnPlacementTimerTick;

        _viewModel.ShowWindowRequested += OnShowWindowRequested;
        _viewModel.ExitRequested += OnExitRequested;
        SourceInitialized += OnSourceInitialized;
        StateChanged += OnWindowStateChanged;
        Closing += OnWindowClosing;
        Closed += (_, _) => _placementTimer.Stop();
        LocationChanged += OnPlacementChanged;
        SizeChanged += OnPlacementChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        RestoreWindowPlacement();
    }

    private void RestoreWindowPlacement()
    {
        var app = _viewModel.SettingsData.Application;
        _restoringPlacement = true;
        try
        {
            Width = app.WindowWidth;
            Height = app.WindowHeight;

            if (app.WindowLeft is { } left && app.WindowTop is { } top &&
                IsOnScreen(left, top, Width, Height))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }
            else
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            if (app.WindowMaximized)
                WindowState = WindowState.Maximized;
        }
        finally
        {
            _restoringPlacement = false;
        }
    }

    private static bool IsOnScreen(double left, double top, double width, double height)
    {
        // Keep a reasonable part of the window on a currently connected display.
        return left + width > SystemParameters.VirtualScreenLeft + 80 &&
               top + height > SystemParameters.VirtualScreenTop + 80 &&
               left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 80 &&
               top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 80;
    }

    private void OnPlacementChanged(object? sender, EventArgs e)
    {
        if (_restoringPlacement) return;
        _placementTimer.Stop();
        _placementTimer.Start();
    }

    private void OnPlacementTimerTick(object? sender, EventArgs e)
    {
        _placementTimer.Stop();
        SavePlacement();
    }

    private void SavePlacement()
    {
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;

        if (bounds.IsEmpty || bounds.Width < 1 || bounds.Height < 1) return;

        _viewModel.SaveWindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            WindowState == WindowState.Maximized);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control)
            return;

        var page = e.Key switch
        {
            Key.D1 or Key.NumPad1 => "dashboard",
            Key.D2 or Key.NumPad2 => "bass",
            Key.D3 or Key.NumPad3 => "volume",
            Key.D4 or Key.NumPad4 => "equalizer",
            Key.D5 or Key.NumPad5 => "profiles",
            Key.D6 or Key.NumPad6 => "settings",
            _ => null
        };

        if (page is not null)
        {
            _viewModel.NavigateCommand.Execute(page);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.M)
        {
            _viewModel.ProcessingEnabled = !_viewModel.ProcessingEnabled;
            e.Handled = true;
        }
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
        SavePlacement();

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
        SavePlacement();
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
