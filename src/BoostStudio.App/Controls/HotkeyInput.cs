using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BoostStudio.App.Services;

namespace BoostStudio.App.Controls;

/// <summary>Focus and press a key combination to capture a global shortcut.</summary>
public sealed class HotkeyInput : Control
{
    private static readonly Typeface ValueTypeface = new(
        new FontFamily("Segoe UI Variable Text, Segoe UI"),
        FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private static readonly Typeface HintTypeface = new(
        new FontFamily("Segoe UI Variable Text, Segoe UI"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private static readonly Brush BackgroundBrush = Freeze(new SolidColorBrush(Color.FromRgb(12, 19, 26)));
    private static readonly Brush OutlineBrush = Freeze(new SolidColorBrush(Color.FromRgb(39, 54, 68)));
    private static readonly Brush ValueBrush = Freeze(new SolidColorBrush(Color.FromRgb(235, 242, 248)));
    private static readonly Brush MutedBrush = Freeze(new SolidColorBrush(Color.FromRgb(120, 140, 158)));
    private static readonly Brush ErrorBrush = Freeze(new SolidColorBrush(Color.FromRgb(249, 112, 102)));

    private bool _capturing;
    private string? _hint;
    private DispatcherTimer? _hintTimer;

    static HotkeyInput()
    {
        FocusableProperty.OverrideMetadata(typeof(HotkeyInput), new FrameworkPropertyMetadata(true));
        IsTabStopProperty.OverrideMetadata(typeof(HotkeyInput), new FrameworkPropertyMetadata(true));
    }

    public HotkeyInput()
    {
        Cursor = Cursors.Hand;
        UseLayoutRounding = true;
        FocusVisualStyle = null;
        Height = 42;
    }

    public static readonly DependencyProperty GestureProperty = DependencyProperty.Register(
        nameof(Gesture), typeof(string), typeof(HotkeyInput),
        new FrameworkPropertyMetadata(string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(HotkeyInput),
        new FrameworkPropertyMetadata(Freeze(new SolidColorBrush(Color.FromRgb(53, 224, 195))),
            FrameworkPropertyMetadataOptions.AffectsRender));

    public string Gesture
    {
        get => (string)GetValue(GestureProperty);
        set => SetValue(GestureProperty, value);
    }

    public Brush Accent
    {
        get => (Brush)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 10 || height < 10) return;

        var accent = Accent ?? OutlineBrush;
        var accentColor = accent is SolidColorBrush solid ? solid.Color : Color.FromRgb(53, 224, 195);
        var border = _capturing || IsKeyboardFocused
            ? new Pen(new SolidColorBrush(Color.FromArgb(190, accentColor.R, accentColor.G, accentColor.B)), 1.4)
            : new Pen(OutlineBrush, 1);
        var rect = new Rect(0.5, 0.5, Math.Max(0, width - 1), Math.Max(0, height - 1));
        dc.DrawRoundedRectangle(BackgroundBrush, border, rect, 9, 9);

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var text = _capturing
            ? "Press a shortcut…"
            : string.IsNullOrWhiteSpace(Gesture) ? "Not set" : Gesture;
        var typeface = _hint is not null ? HintTypeface : ValueTypeface;
        var brush = _hint is not null ? ErrorBrush : string.IsNullOrWhiteSpace(Gesture) ? MutedBrush : ValueBrush;
        var fontSize = _hint is not null ? 10.5 : 12;
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            typeface, fontSize, brush, pixelsPerDip);
        dc.DrawText(formatted, new Point(14, height / 2 - formatted.Height / 2));

        if (_capturing)
        {
            var dotBrush = new SolidColorBrush(Color.FromArgb(220, accentColor.R, accentColor.G, accentColor.B));
            dotBrush.Freeze();
            dc.DrawEllipse(dotBrush, null, new Point(width - 15, height / 2), 3, 3);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        _capturing = true;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        _capturing = true;
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        _capturing = false;
        InvalidateVisual();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (!IsKeyboardFocused) return;

        if (e.Key == Key.Tab)
        {
            _capturing = false;
            InvalidateVisual();
            MoveFocus(new TraversalRequest(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
                ? FocusNavigationDirection.Previous
                : FocusNavigationDirection.Next));
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            _capturing = false;
            InvalidateVisual();
            MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            e.Handled = true;
            return;
        }

        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System)
        {
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Back or Key.Delete)
        {
            SetCurrentValue(GestureProperty, string.Empty);
            ShowHint("Shortcut cleared");
            e.Handled = true;
            return;
        }

        var key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        var gesture = HotkeyService.Format(Keyboard.Modifiers, key);
        if (!HotkeyService.TryParse(gesture, out _, out var error))
        {
            ShowHint(error ?? "Use a modifier plus a key.");
            e.Handled = true;
            return;
        }

        SetCurrentValue(GestureProperty, gesture);
        _hint = null;
        _capturing = false;
        InvalidateVisual();
        MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        e.Handled = true;
    }

    private void ShowHint(string hint)
    {
        _hint = hint;
        InvalidateVisual();
        _hintTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _hintTimer.Stop();
        _hintTimer.Tick -= OnHintTimer;
        _hintTimer.Tick += OnHintTimer;
        _hintTimer.Start();
    }

    private void OnHintTimer(object? sender, EventArgs e)
    {
        _hintTimer?.Stop();
        _hint = null;
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
