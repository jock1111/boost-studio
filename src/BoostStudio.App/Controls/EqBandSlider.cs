using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace BoostStudio.App.Controls;

/// <summary>Vertical equalizer band control centred on 0 dB, with direct drag and keyboard edit.</summary>
public sealed class EqBandSlider : FrameworkElement
{
    private static readonly Typeface ValueTypeface = new(
        new FontFamily("Segoe UI Variable Display, Segoe UI"),
        FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private static readonly Typeface LabelTypeface = new(
        new FontFamily("Segoe UI Variable Text, Segoe UI"),
        FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);

    private static readonly Brush TrackBrush = Freeze(new SolidColorBrush(Color.FromRgb(30, 43, 56)));
    private static readonly Brush BorderBrush = Freeze(new SolidColorBrush(Color.FromRgb(42, 58, 73)));
    private static readonly Brush ValueBrush = Freeze(new SolidColorBrush(Color.FromRgb(235, 242, 248)));
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(128, 148, 166)));
    private static readonly Brush TickBrush = Freeze(new SolidColorBrush(Color.FromArgb(100, 110, 132, 150)));

    private bool _dragging;

    static EqBandSlider()
    {
        FocusableProperty.OverrideMetadata(typeof(EqBandSlider), new FrameworkPropertyMetadata(true));
    }

    public EqBandSlider()
    {
        Cursor = Cursors.SizeNS;
        UseLayoutRounding = true;
        FocusVisualStyle = null;
    }

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(EqBandSlider),
        new FrameworkPropertyMetadata(0d,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender,
            null, Coerce));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(EqBandSlider),
        new FrameworkPropertyMetadata(-12d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(EqBandSlider),
        new FrameworkPropertyMetadata(12d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(double), typeof(EqBandSlider),
        new FrameworkPropertyMetadata(0.5, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BandLabelProperty = DependencyProperty.Register(
        nameof(BandLabel), typeof(string), typeof(EqBandSlider),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(EqBandSlider),
        new FrameworkPropertyMetadata(Freeze(new SolidColorBrush(Color.FromRgb(56, 189, 248))),
            FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Step
    {
        get => (double)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    public string BandLabel
    {
        get => (string)GetValue(BandLabelProperty);
        set => SetValue(BandLabelProperty, value);
    }

    public Brush Accent
    {
        get => (Brush)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    private static object Coerce(DependencyObject d, object baseValue)
    {
        var control = (EqBandSlider)d;
        var value = (double)baseValue;
        if (double.IsNaN(value)) value = 0;
        return Math.Clamp(value, Math.Min(control.Minimum, control.Maximum),
            Math.Max(control.Minimum, control.Maximum));
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 20 || height < 90) return;

        if (!IsEnabled) dc.PushOpacity(0.42);

        var accent = Accent ?? TrackBrush;
        var accentColor = accent is SolidColorBrush solid ? solid.Color : Color.FromRgb(56, 189, 248);
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var centerX = width / 2;
        var top = 34.0;
        var bottom = height - 34.0;
        var trackHeight = Math.Max(20, bottom - top);
        var range = Math.Max(0.001, Maximum - Minimum);

        var valueText = new FormattedText(
            Value.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture),
            CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, ValueTypeface, 11.5,
            Math.Abs(Value) < 0.05 ? LabelBrush : ValueBrush, pixelsPerDip);
        dc.DrawText(valueText, new Point(centerX - valueText.Width / 2, 4));

        var trackRect = new Rect(centerX - 3, top, 6, trackHeight);
        dc.DrawRoundedRectangle(TrackBrush, new Pen(BorderBrush, 1), trackRect, 3, 3);

        var zeroY = ValueToY(0, top, trackHeight);
        var valueY = ValueToY(Value, top, trackHeight);
        var fillTop = Math.Min(zeroY, valueY);
        var fillHeight = Math.Max(1, Math.Abs(zeroY - valueY));
        if (fillHeight > 1.2)
        {
            var fill = new LinearGradientBrush(
                Color.FromArgb(255, accentColor.R, accentColor.G, accentColor.B),
                Color.FromArgb(110, accentColor.R, accentColor.G, accentColor.B), 90);
            fill.Freeze();
            dc.DrawRoundedRectangle(fill, null, new Rect(centerX - 3, fillTop, 6, fillHeight), 3, 3);
        }

        var tickPen = new Pen(TickBrush, 1) { DashStyle = DashStyles.Dot };
        foreach (var tick in new[] { -12d, -6, 0, 6, 12 })
        {
            if (tick < Minimum || tick > Maximum) continue;
            var y = ValueToY(tick, top, trackHeight);
            dc.DrawLine(tickPen, new Point(centerX - 12, y), new Point(centerX - 8, y));
            dc.DrawLine(tickPen, new Point(centerX + 8, y), new Point(centerX + 12, y));
        }

        dc.DrawLine(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(80, 160, 178, 194))), 1)
            { DashStyle = DashStyles.Dash }, new Point(centerX - 16, zeroY), new Point(centerX + 16, zeroY));

        var glow = new SolidColorBrush(Color.FromArgb(36, accentColor.R, accentColor.G, accentColor.B));
        glow.Freeze();
        dc.DrawEllipse(glow, null, new Point(centerX, valueY), 18, 18);

        var handleRect = new Rect(centerX - Math.Min(24, width * 0.48), valueY - 3.5,
            Math.Min(48, width * 0.96), 7);
        var handleBrush = new LinearGradientBrush(
            Color.FromArgb(255, 235, 246, 248),
            Color.FromArgb(255, accentColor.R, accentColor.G, accentColor.B), 0);
        handleBrush.Freeze();
        dc.DrawRoundedRectangle(handleBrush, new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 0.8),
            handleRect, 3.5, 3.5);

        if (!string.IsNullOrWhiteSpace(BandLabel))
        {
            var label = new FormattedText(BandLabel.ToUpperInvariant(), CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, LabelTypeface, 9.5, LabelBrush, pixelsPerDip);
            dc.DrawText(label, new Point(centerX - label.Width / 2, height - 20));
        }

        if (IsKeyboardFocused)
        {
            dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(150, accentColor.R, accentColor.G, accentColor.B)), 1.2)
            {
                DashStyle = DashStyles.Dot
            }, new Rect(4, 2, width - 8, height - 4), 10, 10);
        }

        if (!IsEnabled) dc.Pop();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!IsEnabled) return;
        if (e.ClickCount == 2)
        {
            _dragging = false;
            ReleaseMouseCapture();
            SetUserValue(0);
            e.Handled = true;
            return;
        }

        Focus();
        _dragging = true;
        CaptureMouse();
        SetFromPoint(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed) return;
        SetFromPoint(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _dragging = false;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!IsEnabled) return;
        SetUserValue(Value + Math.Sign(e.Delta) * (Step <= 0 ? 0.5 : Step));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!IsEnabled) return;
        var step = Step <= 0 ? 0.5 : Step;
        switch (e.Key)
        {
            case Key.Up:
            case Key.Right:
                SetUserValue(Value + step);
                e.Handled = true;
                break;
            case Key.Down:
            case Key.Left:
                SetUserValue(Value - step);
                e.Handled = true;
                break;
            case Key.PageUp:
                SetUserValue(Value + step * 4);
                e.Handled = true;
                break;
            case Key.PageDown:
                SetUserValue(Value - step * 4);
                e.Handled = true;
                break;
            case Key.Home:
                SetUserValue(Maximum);
                e.Handled = true;
                break;
            case Key.End:
                SetUserValue(Minimum);
                e.Handled = true;
                break;
            case Key.Delete:
            case Key.Back:
                SetUserValue(0);
                e.Handled = true;
                break;
        }
    }

    private void SetFromPoint(Point point)
    {
        var top = 34.0;
        var bottom = ActualHeight - 34.0;
        var trackHeight = Math.Max(20, bottom - top);
        var fraction = Math.Clamp((bottom - point.Y) / trackHeight, 0, 1);
        var value = Minimum + fraction * (Maximum - Minimum);
        SetUserValue(value);
    }

    private void SetUserValue(double value)
    {
        var step = Step <= 0 ? 0.5 : Step;
        value = Math.Round(value / step, MidpointRounding.AwayFromZero) * step;
        value = Math.Clamp(value, Minimum, Maximum);
        value = Math.Round(value, 1);
        SetCurrentValue(ValueProperty, value);
    }

    private double ValueToY(double value, double top, double trackHeight)
    {
        var range = Math.Max(0.001, Maximum - Minimum);
        var fraction = Math.Clamp((value - Minimum) / range, 0, 1);
        return top + (1 - fraction) * trackHeight;
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
