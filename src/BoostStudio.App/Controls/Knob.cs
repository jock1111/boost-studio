using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace BoostStudio.App.Controls;

/// <summary>
/// Reusable rotary knob with drag, wheel and keyboard support. Uses direct rendering
/// so the arc, glow and typography stay crisp at any size.
/// </summary>
public sealed class Knob : FrameworkElement
{
    private const double StartAngle = 140;
    private const double SweepAngle = 260;

    private static readonly Typeface ValueTypeface = new(
        new FontFamily("Segoe UI Variable Display, Segoe UI"),
        FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private static readonly Typeface LabelTypeface = new(
        new FontFamily("Segoe UI Variable Text, Segoe UI"),
        FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);

    private static readonly Brush TrackBrush = Freeze(new SolidColorBrush(Color.FromRgb(32, 45, 58)));
    private static readonly Brush ValueBrush = Freeze(new SolidColorBrush(Color.FromRgb(241, 245, 249)));
    private static readonly Brush MutedBrush = Freeze(new SolidColorBrush(Color.FromRgb(130, 148, 165)));
    private static readonly Brush CenterBrush = Freeze(CreateCenterBrush());

    private bool _dragging;
    private double _dragStartY;
    private double _dragStartValue;

    static Knob()
    {
        FocusableProperty.OverrideMetadata(typeof(Knob), new FrameworkPropertyMetadata(true));
    }

    public Knob()
    {
        Cursor = Cursors.SizeNS;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        FocusVisualStyle = null;
    }

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(Knob),
        new FrameworkPropertyMetadata(0d,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender,
            OnValueChanged, CoerceValue));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(Knob),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Knob),
        new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(double), typeof(Knob),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ResetValueProperty = DependencyProperty.Register(
        nameof(ResetValue), typeof(double), typeof(Knob),
        new FrameworkPropertyMetadata(double.NaN));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(Knob),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SuffixProperty = DependencyProperty.Register(
        nameof(Suffix), typeof(string), typeof(Knob),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ValueFormatProperty = DependencyProperty.Register(
        nameof(ValueFormat), typeof(string), typeof(Knob),
        new FrameworkPropertyMetadata("0", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(Knob),
        new FrameworkPropertyMetadata(Freeze(new SolidColorBrush(Color.FromRgb(53, 224, 195))),
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(Knob),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

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

    public double ResetValue
    {
        get => (double)GetValue(ResetValueProperty);
        set => SetValue(ResetValueProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Suffix
    {
        get => (string)GetValue(SuffixProperty);
        set => SetValue(SuffixProperty, value);
    }

    public string ValueFormat
    {
        get => (string)GetValue(ValueFormatProperty);
        set => SetValue(ValueFormatProperty, value);
    }

    public Brush Accent
    {
        get => (Brush)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private static object CoerceValue(DependencyObject d, object baseValue)
    {
        var knob = (Knob)d;
        var value = (double)baseValue;
        if (double.IsNaN(value)) value = knob.Minimum;
        return Math.Clamp(value, knob.Minimum, knob.Maximum);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var knob = (Knob)d;
        knob.InvalidateVisual();
        knob.RaiseEvent(new RoutedPropertyChangedEventArgs<double>(
            (double)e.OldValue, (double)e.NewValue, ValueChangedEvent));
    }

    public static readonly RoutedEvent ValueChangedEvent = EventManager.RegisterRoutedEvent(
        nameof(ValueChanged), RoutingStrategy.Bubble,
        typeof(RoutedPropertyChangedEventHandler<double>), typeof(Knob));

    public event RoutedPropertyChangedEventHandler<double> ValueChanged
    {
        add => AddHandler(ValueChangedEvent, value);
        remove => RemoveHandler(ValueChangedEvent, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var accent = Accent ?? TrackBrush;
        var accentColor = accent is SolidColorBrush solid ? solid.Color : Color.FromRgb(53, 224, 195);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 36 || height < 36) return;

        if (!IsEnabled)
            dc.PushOpacity(0.38);
        else if (!IsActive)
            dc.PushOpacity(0.62);

        var labelHeight = 24.0;
        var contentHeight = Math.Max(30, height - labelHeight);
        var radius = Math.Max(14, Math.Min(width / 2 - 18, (contentHeight - 38) / 2));
        var center = new Point(width / 2, contentHeight / 2 - 2);
        var fraction = Maximum > Minimum ? Math.Clamp((Value - Minimum) / (Maximum - Minimum), 0, 1) : 0;
        var valueAngle = StartAngle + SweepAngle * fraction;

        DrawTicks(dc, center, radius, accentColor);

        var trackGeometry = BuildArc(center, radius, StartAngle, SweepAngle);
        dc.DrawGeometry(null, new Pen(TrackBrush, 7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, trackGeometry);

        if (Math.Abs(fraction) > 0.001)
        {
            var activeGeometry = BuildArc(center, radius, StartAngle, SweepAngle * fraction);
            var activeBrush = new LinearGradientBrush(
                Color.FromArgb(255, accentColor.R, accentColor.G, accentColor.B),
                Color.FromArgb(255, Math.Min((byte)255, (byte)(accentColor.R + 45)),
                    Math.Min((byte)255, (byte)(accentColor.G + 45)),
                    Math.Min((byte)255, (byte)(accentColor.B + 45))),
                new Point(0, 1), new Point(1, 0));
            activeBrush.Freeze();

            dc.DrawGeometry(null, new Pen(BrushWithOpacity(accentColor, 0.20), 13)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            }, activeGeometry);
            dc.DrawGeometry(null, new Pen(activeBrush, 7)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            }, activeGeometry);
        }

        var pointerAngle = valueAngle * Math.PI / 180.0;
        var pointerInner = PointOnCircle(center, radius - 11, pointerAngle);
        var pointerOuter = PointOnCircle(center, radius + 7, pointerAngle);
        dc.DrawLine(new Pen(BrushWithOpacity(accentColor, 0.35), 7)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        }, pointerInner, pointerOuter);
        dc.DrawLine(new Pen(Freeze(new SolidColorBrush(Lighten(accentColor, 0.45))), 2.4)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        }, pointerInner, pointerOuter);

        dc.DrawEllipse(CenterBrush, new Pen(BrushWithOpacity(accentColor, 0.13), 1), center, radius - 20, radius - 20);

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var text = FormatValue();
        var valueText = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            ValueTypeface, Math.Clamp(radius * 0.42, 17, 27), ValueBrush, pixelsPerDip);
        dc.DrawText(valueText, new Point(center.X - valueText.Width / 2, center.Y - valueText.Height / 2 - 2));

        if (!string.IsNullOrWhiteSpace(Title))
        {
            var titleText = new FormattedText(Title.ToUpperInvariant(), CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, LabelTypeface, 10, MutedBrush, pixelsPerDip);
            dc.DrawText(titleText, new Point(width / 2 - titleText.Width / 2, height - 17));
        }

        if (IsKeyboardFocused)
        {
            var focusPen = new Pen(BrushWithOpacity(accentColor, 0.55), 1.2) { DashStyle = DashStyles.Dot };
            dc.DrawEllipse(null, focusPen, center, radius + 14, radius + 14);
        }

        if (!IsEnabled || !IsActive)
            dc.Pop();
    }

    private string FormatValue()
    {
        var numeric = Value.ToString(string.IsNullOrWhiteSpace(ValueFormat) ? "0" : ValueFormat,
            CultureInfo.CurrentCulture);
        return string.IsNullOrEmpty(Suffix) ? numeric : $"{numeric}{Suffix}";
    }

    private static void DrawTicks(DrawingContext dc, Point center, double radius, Color accentColor)
    {
        for (var i = 0; i <= 10; i++)
        {
            var angle = (StartAngle + SweepAngle * i / 10) * Math.PI / 180.0;
            var isMajor = i is 0 or 5 or 10;
            var inner = PointOnCircle(center, radius + 12, angle);
            var outer = PointOnCircle(center, radius + (isMajor ? 17 : 15), angle);
            var brush = isMajor
                ? BrushWithOpacity(accentColor, 0.45)
                : Freeze(new SolidColorBrush(Color.FromArgb(90, 118, 138, 156)));
            dc.DrawLine(new Pen(brush, isMajor ? 1.6 : 1), inner, outer);
        }
    }

    private static Geometry BuildArc(Point center, double radius, double startAngle, double sweepAngle)
    {
        var start = PointOnCircle(center, radius, startAngle * Math.PI / 180.0);
        var end = PointOnCircle(center, radius, (startAngle + sweepAngle) * Math.PI / 180.0);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(radius, radius), 0,
                Math.Abs(sweepAngle) > 180,
                sweepAngle >= 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise,
                true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    private static Point PointOnCircle(Point center, double radius, double radians) =>
        new(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!IsEnabled) return;
        if (e.ClickCount == 2)
        {
            _dragging = false;
            ReleaseMouseCapture();
            SetUserValue(double.IsNaN(ResetValue) ? Minimum : ResetValue);
            e.Handled = true;
            return;
        }

        Focus();
        _dragging = true;
        _dragStartY = e.GetPosition(this).Y;
        _dragStartValue = Value;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed) return;
        var range = Maximum - Minimum;
        if (range <= 0) return;

        var delta = (_dragStartY - e.GetPosition(this).Y) / 170.0 * range;
        SetUserValue(_dragStartValue + delta);
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
        var step = Step <= 0 ? (Maximum - Minimum) / 100 : Step;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) step *= 5;
        SetUserValue(Value + Math.Sign(e.Delta) * step);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!IsEnabled) return;
        var step = Step <= 0 ? (Maximum - Minimum) / 100 : Step;
        switch (e.Key)
        {
            case Key.Left:
            case Key.Down:
                SetUserValue(Value - step);
                e.Handled = true;
                break;
            case Key.Right:
            case Key.Up:
                SetUserValue(Value + step);
                e.Handled = true;
                break;
            case Key.PageDown:
                SetUserValue(Value - step * 5);
                e.Handled = true;
                break;
            case Key.PageUp:
                SetUserValue(Value + step * 5);
                e.Handled = true;
                break;
            case Key.Home:
                SetUserValue(Minimum);
                e.Handled = true;
                break;
            case Key.End:
                SetUserValue(Maximum);
                e.Handled = true;
                break;
            case Key.Enter when double.IsNaN(ResetValue):
                break;
        }
    }

    private void SetUserValue(double value)
    {
        var step = Step <= 0 ? 0 : Step;
        var snapped = step > 0 ? Math.Round(value / step, MidpointRounding.AwayFromZero) * step : value;
        snapped = Math.Clamp(snapped, Minimum, Maximum);
        SetCurrentValue(ValueProperty, snapped);
    }

    private static Brush BrushWithOpacity(Color color, double opacity) =>
        Freeze(new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(opacity * 255, 0, 255), color.R, color.G, color.B)));

    private static Color Lighten(Color color, double amount) => Color.FromRgb(
        (byte)Math.Min(255, color.R + (255 - color.R) * amount),
        (byte)Math.Min(255, color.G + (255 - color.G) * amount),
        (byte)Math.Min(255, color.B + (255 - color.B) * amount));

    private static Brush CreateCenterBrush()
    {
        var brush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.35),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.8,
            RadiusY = 0.8
        };
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(18, 27, 36), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(11, 17, 23), 1));
        return brush;
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
