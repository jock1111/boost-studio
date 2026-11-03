using System.Windows;
using System.Windows.Media;

namespace BoostStudio.App.Controls;

/// <summary>Segmented peak/RMS meter with a peak-hold marker. Supports horizontal and vertical layouts.</summary>
public sealed class LevelMeter : FrameworkElement
{
    private static readonly Brush TrackBrush = Freeze(new SolidColorBrush(Color.FromRgb(23, 34, 44)));
    private static readonly Brush BorderBrush = Freeze(new SolidColorBrush(Color.FromRgb(40, 55, 69)));
    private static readonly Brush DividerBrush = Freeze(new SolidColorBrush(Color.FromArgb(150, 10, 15, 20)));
    private static readonly Brush PeakBrush = Freeze(new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)));
    private static readonly Brush RmsBrush = Freeze(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)));

    static LevelMeter()
    {
        SnapsToDevicePixelsProperty.OverrideMetadata(typeof(LevelMeter),
            new FrameworkPropertyMetadata(true));
    }

    public static readonly DependencyProperty PeakDbProperty = DependencyProperty.Register(
        nameof(PeakDb), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(-120d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RmsDbProperty = DependencyProperty.Register(
        nameof(RmsDb), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(-120d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PeakHoldDbProperty = DependencyProperty.Register(
        nameof(PeakHoldDb), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(-120d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinimumDbProperty = DependencyProperty.Register(
        nameof(MinimumDb), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(-60d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumDbProperty = DependencyProperty.Register(
        nameof(MaximumDb), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation), typeof(System.Windows.Controls.Orientation), typeof(LevelMeter),
        new FrameworkPropertyMetadata(System.Windows.Controls.Orientation.Horizontal,
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ClippingProperty = DependencyProperty.Register(
        nameof(Clipping), typeof(bool), typeof(LevelMeter),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public double PeakDb
    {
        get => (double)GetValue(PeakDbProperty);
        set => SetValue(PeakDbProperty, value);
    }

    public double RmsDb
    {
        get => (double)GetValue(RmsDbProperty);
        set => SetValue(RmsDbProperty, value);
    }

    public double PeakHoldDb
    {
        get => (double)GetValue(PeakHoldDbProperty);
        set => SetValue(PeakHoldDbProperty, value);
    }

    public double MinimumDb
    {
        get => (double)GetValue(MinimumDbProperty);
        set => SetValue(MinimumDbProperty, value);
    }

    public double MaximumDb
    {
        get => (double)GetValue(MaximumDbProperty);
        set => SetValue(MaximumDbProperty, value);
    }

    public System.Windows.Controls.Orientation Orientation
    {
        get => (System.Windows.Controls.Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    public bool Clipping
    {
        get => (bool)GetValue(ClippingProperty);
        set => SetValue(ClippingProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 2 || height < 2) return;

        var rect = new Rect(0, 0, width, height);
        var radius = Orientation == System.Windows.Controls.Orientation.Horizontal
            ? Math.Min(height / 2, 9)
            : Math.Min(width / 2, 9);

        dc.DrawRoundedRectangle(TrackBrush, new Pen(BorderBrush, 1), rect, radius, radius);

        var peakFraction = Fraction(PeakDb);
        var rmsFraction = Fraction(RmsDb);
        var holdFraction = Fraction(PeakHoldDb);

        var gradient = new LinearGradientBrush
        {
            StartPoint = Orientation == System.Windows.Controls.Orientation.Horizontal ? new Point(0, 0.5) : new Point(0.5, 1),
            EndPoint = Orientation == System.Windows.Controls.Orientation.Horizontal ? new Point(1, 0.5) : new Point(0.5, 0),
            MappingMode = BrushMappingMode.RelativeToBoundingBox
        };
        gradient.GradientStops.Add(new GradientStop(Color.FromRgb(45, 212, 191), 0));
        gradient.GradientStops.Add(new GradientStop(Color.FromRgb(94, 234, 212), 0.45));
        gradient.GradientStops.Add(new GradientStop(Color.FromRgb(163, 230, 137), 0.68));
        gradient.GradientStops.Add(new GradientStop(Color.FromRgb(245, 201, 93), 0.84));
        gradient.GradientStops.Add(new GradientStop(Color.FromRgb(249, 112, 102), 1));
        gradient.Freeze();

        if (peakFraction > 0.001)
        {
            var active = Orientation == System.Windows.Controls.Orientation.Horizontal
                ? new Rect(0, 0, width * peakFraction, height)
                : new Rect(0, height * (1 - peakFraction), width, height * peakFraction);

            dc.PushClip(new RectangleGeometry(rect, radius, radius));
            dc.DrawRectangle(gradient, null, active);
            dc.Pop();

            var segments = Orientation == System.Windows.Controls.Orientation.Horizontal ? (int)(width / 6) : (int)(height / 6);
            segments = Math.Clamp(segments, 12, 64);
            var dividerPen = new Pen(DividerBrush, 1);
            for (var i = 1; i < segments; i++)
            {
                var position = Orientation == System.Windows.Controls.Orientation.Horizontal
                    ? width * i / segments
                    : height * i / segments;
                if (Orientation == System.Windows.Controls.Orientation.Horizontal)
                    dc.DrawLine(dividerPen, new Point(position, 1), new Point(position, height - 1));
                else
                    dc.DrawLine(dividerPen, new Point(1, position), new Point(width - 1, position));
            }
        }

        if (rmsFraction > 0.001)
        {
            var rmsPen = new Pen(RmsBrush, 1.5);
            if (Orientation == System.Windows.Controls.Orientation.Horizontal)
            {
                var x = width * rmsFraction;
                dc.DrawLine(rmsPen, new Point(x, 2), new Point(x, height - 2));
            }
            else
            {
                var y = height * (1 - rmsFraction);
                dc.DrawLine(rmsPen, new Point(2, y), new Point(width - 2, y));
            }
        }

        if (holdFraction > 0.001)
        {
            var holdPen = new Pen(PeakBrush, 2.2);
            if (Orientation == System.Windows.Controls.Orientation.Horizontal)
            {
                var x = Math.Clamp(width * holdFraction, 1, width - 1);
                dc.DrawLine(holdPen, new Point(x, 1.5), new Point(x, height - 1.5));
            }
            else
            {
                var y = Math.Clamp(height * (1 - holdFraction), 1, height - 1);
                dc.DrawLine(holdPen, new Point(1.5, y), new Point(width - 1.5, y));
            }
        }

        if (Clipping)
        {
            var clipPen = new Pen(Freeze(new SolidColorBrush(Color.FromArgb(220, 249, 112, 102))), 1.6);
            dc.DrawRoundedRectangle(null, clipPen, new Rect(0.8, 0.8, Math.Max(0, width - 1.6), Math.Max(0, height - 1.6)),
                radius, radius);
        }
    }

    private double Fraction(double db)
    {
        var minimum = MinimumDb;
        var maximum = MaximumDb;
        if (maximum <= minimum) return 0;
        return Math.Clamp((db - minimum) / (maximum - minimum), 0, 1);
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
