using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace BoostStudio.App.Controls;

/// <summary>
/// Frequency response graph driven by actual biquad coefficients from the DSP layer.
/// Uses a logarithmic frequency axis and optionally displays live measured energy.
/// </summary>
public sealed class ResponseCurve : FrameworkElement
{
    private static readonly Typeface LabelTypeface = new(
        new FontFamily("Segoe UI Variable Text, Segoe UI"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private static readonly Brush GridBrush = Freeze(new SolidColorBrush(Color.FromArgb(42, 130, 148, 165)));
    private static readonly Brush ZeroBrush = Freeze(new SolidColorBrush(Color.FromArgb(95, 160, 178, 194)));
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(112, 132, 150)));
    private static readonly Brush LiveBrush = Freeze(new SolidColorBrush(Color.FromArgb(42, 139, 92, 246)));

    private static readonly double[] FrequencyGrid = { 20, 31.5, 50, 63, 100, 125, 200, 250, 500, 1000, 2000, 4000, 8000, 16000, 20000 };

    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(double[]), typeof(ResponseCurve),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LiveBarsProperty = DependencyProperty.Register(
        nameof(LiveBars), typeof(float[]), typeof(ResponseCurve),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinFrequencyProperty = DependencyProperty.Register(
        nameof(MinFrequency), typeof(double), typeof(ResponseCurve),
        new FrameworkPropertyMetadata(20d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaxFrequencyProperty = DependencyProperty.Register(
        nameof(MaxFrequency), typeof(double), typeof(ResponseCurve),
        new FrameworkPropertyMetadata(20000d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinDbProperty = DependencyProperty.Register(
        nameof(MinDb), typeof(double), typeof(ResponseCurve),
        new FrameworkPropertyMetadata(-15d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaxDbProperty = DependencyProperty.Register(
        nameof(MaxDb), typeof(double), typeof(ResponseCurve),
        new FrameworkPropertyMetadata(15d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(ResponseCurve),
        new FrameworkPropertyMetadata(Freeze(new SolidColorBrush(Color.FromRgb(53, 224, 195))),
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowLabelsProperty = DependencyProperty.Register(
        nameof(ShowLabels), typeof(bool), typeof(ResponseCurve),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public double[]? Values
    {
        get => (double[]?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public float[]? LiveBars
    {
        get => (float[]?)GetValue(LiveBarsProperty);
        set => SetValue(LiveBarsProperty, value);
    }

    public double MinFrequency
    {
        get => (double)GetValue(MinFrequencyProperty);
        set => SetValue(MinFrequencyProperty, value);
    }

    public double MaxFrequency
    {
        get => (double)GetValue(MaxFrequencyProperty);
        set => SetValue(MaxFrequencyProperty, value);
    }

    public double MinDb
    {
        get => (double)GetValue(MinDbProperty);
        set => SetValue(MinDbProperty, value);
    }

    public double MaxDb
    {
        get => (double)GetValue(MaxDbProperty);
        set => SetValue(MaxDbProperty, value);
    }

    public Brush Accent
    {
        get => (Brush)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public bool ShowLabels
    {
        get => (bool)GetValue(ShowLabelsProperty);
        set => SetValue(ShowLabelsProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 30 || height < 30) return;

        var accent = Accent ?? ZeroBrush;
        var accentColor = accent is SolidColorBrush solid ? solid.Color : Color.FromRgb(53, 224, 195);
        var left = ShowLabels ? 36.0 : 4.0;
        var right = ShowLabels ? 10.0 : 4.0;
        var top = 8.0;
        var bottom = ShowLabels ? 22.0 : 4.0;
        var plotWidth = Math.Max(1, width - left - right);
        var plotHeight = Math.Max(1, height - top - bottom);
        var plot = new Rect(left, top, plotWidth, plotHeight);
        var minDb = Math.Min(MinDb, MaxDb - 1);
        var maxDb = Math.Max(MaxDb, MinDb + 1);

        dc.PushClip(new RectangleGeometry(plot));
        DrawGrid(dc, plot, minDb, maxDb);
        DrawLiveBars(dc, plot);
        DrawResponse(dc, plot, minDb, maxDb, accentColor);
        dc.Pop();

        if (ShowLabels)
            DrawLabels(dc, plot, minDb, maxDb);
    }

    private void DrawGrid(DrawingContext dc, Rect plot, double minDb, double maxDb)
    {
        var dbStep = ChooseDbStep(maxDb - minDb);
        var first = Math.Ceiling(minDb / dbStep) * dbStep;
        for (var value = first; value <= maxDb + 0.001; value += dbStep)
        {
            var y = DbToY(value, plot, minDb, maxDb);
            var isZero = Math.Abs(value) < 0.001;
            dc.DrawLine(new Pen(isZero ? ZeroBrush : GridBrush, isZero ? 1.2 : 1)
            {
                DashStyle = isZero ? DashStyles.Dash : DashStyles.Dot
            }, new Point(plot.Left, y), new Point(plot.Right, y));
        }

        foreach (var frequency in FrequencyGrid)
        {
            if (frequency < MinFrequency || frequency > MaxFrequency) continue;
            var x = FrequencyToX(frequency, plot);
            dc.DrawLine(new Pen(GridBrush, 1) { DashStyle = DashStyles.Dot },
                new Point(x, plot.Top), new Point(x, plot.Bottom));
        }
    }

    private void DrawLiveBars(DrawingContext dc, Rect plot)
    {
        var bars = LiveBars;
        if (bars is null || bars.Length == 0) return;

        var start = Math.Log10(Math.Max(1, MinFrequency));
        var end = Math.Log10(Math.Max(MinFrequency * 2, Math.Min(250, MaxFrequency)));
        var span = Math.Max(0.0001, end - start);
        var barWidth = plot.Width / bars.Length;

        for (var i = 0; i < bars.Length; i++)
        {
            var frequency = Math.Pow(10, start + span * (i + 0.5) / bars.Length);
            var x = FrequencyToX(frequency, plot);
            if (x < plot.Left || x > plot.Right) continue;
            var level = Math.Clamp(bars[i], 0, 1);
            if (level < 0.015) continue;
            var barHeight = level * plot.Height * 0.9;
            dc.DrawRectangle(LiveBrush, null,
                new Rect(x, plot.Bottom - barHeight, Math.Max(1, barWidth - 1), barHeight));
        }
    }

    private void DrawResponse(DrawingContext dc, Rect plot, double minDb, double maxDb, Color accentColor)
    {
        var values = Values;
        if (values is null || values.Length < 2) return;

        var points = new List<Point>(values.Length);
        for (var i = 0; i < values.Length; i++)
        {
            var fraction = i / (double)(values.Length - 1);
            var frequency = Math.Pow(10,
                Math.Log10(MinFrequency) + fraction * (Math.Log10(MaxFrequency) - Math.Log10(MinFrequency)));
            var value = double.IsFinite(values[i]) ? Math.Clamp(values[i], minDb, maxDb) : 0;
            points.Add(new Point(FrequencyToX(frequency, plot), DbToY(value, plot, minDb, maxDb)));
        }

        var zeroY = DbToY(Math.Clamp(0, minDb, maxDb), plot, minDb, maxDb);
        var fillGeometry = new StreamGeometry();
        using (var context = fillGeometry.Open())
        {
            context.BeginFigure(new Point(points[0].X, zeroY), true, true);
            foreach (var point in points)
                context.LineTo(point, true, false);
            context.LineTo(new Point(points[^1].X, zeroY), true, false);
        }

        fillGeometry.Freeze();
        var fill = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1)
        };
        fill.GradientStops.Add(new GradientStop(Color.FromArgb(72, accentColor.R, accentColor.G, accentColor.B), 0));
        fill.GradientStops.Add(new GradientStop(Color.FromArgb(12, accentColor.R, accentColor.G, accentColor.B), 0.8));
        fill.GradientStops.Add(new GradientStop(Color.FromArgb(0, accentColor.R, accentColor.G, accentColor.B), 1));
        fill.Freeze();
        dc.DrawGeometry(fill, null, fillGeometry);

        var curve = new StreamGeometry();
        using (var context = curve.Open())
        {
            context.BeginFigure(points[0], false, false);
            for (var i = 1; i < points.Count; i++)
                context.LineTo(points[i], true, false);
        }

        curve.Freeze();
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(40, accentColor.R, accentColor.G, accentColor.B)), 8)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        }, curve);
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(accentColor), 2.2)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        }, curve);

        var glow = new RadianGradientBrushFactory().Create(accentColor);
        var last = points[^1];
        dc.DrawEllipse(glow, null, last, 4.5, 4.5);
    }

    private void DrawLabels(DrawingContext dc, Rect plot, double minDb, double maxDb)
    {
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var dbStep = ChooseDbStep(maxDb - minDb);
        var first = Math.Ceiling(minDb / dbStep) * dbStep;
        for (var value = first; value <= maxDb + 0.001; value += dbStep)
        {
            var y = DbToY(value, plot, minDb, maxDb);
            var label = value == 0 ? "0" : value.ToString("+0;-0;0", CultureInfo.CurrentCulture);
            var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                LabelTypeface, 9.5, LabelBrush, pixelsPerDip);
            dc.DrawText(text, new Point(plot.Left - text.Width - 7, y - text.Height / 2));
        }

        foreach (var frequency in FrequencyGrid)
        {
            if (frequency < MinFrequency || frequency > MaxFrequency) continue;
            if (frequency > 1000 && frequency < 2000 || frequency > 2000 && frequency < 4000 ||
                frequency > 4000 && frequency < 8000 || frequency > 8000 && frequency < 16000)
                continue;

            var x = FrequencyToX(frequency, plot);
            var label = frequency >= 1000 ? $"{frequency / 1000:0.#}k" : $"{frequency:0}";
            var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                LabelTypeface, 9.5, LabelBrush, pixelsPerDip);
            var textX = Math.Clamp(x - text.Width / 2, plot.Left, plot.Right - text.Width);
            dc.DrawText(text, new Point(textX, plot.Bottom + 5));
        }
    }

    private double FrequencyToX(double frequency, Rect plot)
    {
        var min = Math.Log10(Math.Max(1, MinFrequency));
        var max = Math.Log10(Math.Max(MinFrequency * 2, MaxFrequency));
        var position = (Math.Log10(Math.Max(1, frequency)) - min) / Math.Max(0.0001, max - min);
        return plot.Left + Math.Clamp(position, 0, 1) * plot.Width;
    }

    private static double DbToY(double value, Rect plot, double minDb, double maxDb) =>
        plot.Top + (maxDb - value) / Math.Max(0.0001, maxDb - minDb) * plot.Height;

    private static double ChooseDbStep(double range)
    {
        foreach (var step in new[] { 1.0, 2, 3, 6, 12, 24 })
        {
            if (range / step <= 7)
                return step;
        }

        return 48;
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    /// <summary>Creates a soft radial dot for the end of the response curve.</summary>
    private sealed class RadianGradientBrushFactory
    {
        public Brush Create(Color color)
        {
            var brush = new RadialGradientBrush
            {
                Center = new Point(0.5, 0.5),
                GradientOrigin = new Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5
            };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(255, color.R, color.G, color.B), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(55, color.R, color.G, color.B), 0.55));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1));
            brush.Freeze();
            return brush;
        }
    }
}
