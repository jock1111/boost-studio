using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BoostStudio.Core.Dsp;

namespace BoostStudio.App.Controls;

/// <summary>
/// Live log-frequency spectrum with optional bass-region emphasis. Values are normalized
/// 0-1 display levels produced by the real WASAPI loopback analyzer.
/// </summary>
public sealed class SpectrumDisplay : FrameworkElement
{
    private static readonly Typeface LabelTypeface = new(
        new FontFamily("Segoe UI Variable Text, Segoe UI"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private static readonly Brush GridBrush = Freeze(new SolidColorBrush(Color.FromArgb(45, 130, 148, 165)));
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(112, 132, 150)));
    private static readonly Brush IdleBrush = Freeze(new SolidColorBrush(Color.FromRgb(55, 72, 86)));

    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(float[]), typeof(SpectrumDisplay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ActiveProperty = DependencyProperty.Register(
        nameof(Active), typeof(bool), typeof(SpectrumDisplay),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(SpectrumDisplay),
        new FrameworkPropertyMetadata(Freeze(new SolidColorBrush(Color.FromRgb(53, 224, 195))),
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HighlightBassProperty = DependencyProperty.Register(
        nameof(HighlightBass), typeof(bool), typeof(SpectrumDisplay),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinFrequencyProperty = DependencyProperty.Register(
        nameof(MinFrequency), typeof(double), typeof(SpectrumDisplay),
        new FrameworkPropertyMetadata(20d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaxFrequencyProperty = DependencyProperty.Register(
        nameof(MaxFrequency), typeof(double), typeof(SpectrumDisplay),
        new FrameworkPropertyMetadata(20000d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowFrequencyLabelsProperty = DependencyProperty.Register(
        nameof(ShowFrequencyLabels), typeof(bool), typeof(SpectrumDisplay),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public float[]? Values
    {
        get => (float[]?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public bool Active
    {
        get => (bool)GetValue(ActiveProperty);
        set => SetValue(ActiveProperty, value);
    }

    public Brush Accent
    {
        get => (Brush)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public bool HighlightBass
    {
        get => (bool)GetValue(HighlightBassProperty);
        set => SetValue(HighlightBassProperty, value);
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

    public bool ShowFrequencyLabels
    {
        get => (bool)GetValue(ShowFrequencyLabelsProperty);
        set => SetValue(ShowFrequencyLabelsProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 10 || height < 10) return;

        var labelHeight = ShowFrequencyLabels ? 18.0 : 0.0;
        var graphHeight = Math.Max(8, height - labelHeight - 2);
        var accent = Accent ?? IdleBrush;
        var accentColor = accent is SolidColorBrush solid ? solid.Color : Color.FromRgb(53, 224, 195);

        dc.DrawLine(new Pen(GridBrush, 1), new Point(0, graphHeight - 0.5), new Point(width, graphHeight - 0.5));
        for (var i = 1; i <= 3; i++)
        {
            var y = graphHeight * i / 4;
            dc.DrawLine(new Pen(GridBrush, 1) { DashStyle = DashStyles.Dot },
                new Point(0, y), new Point(width, y));
        }

        var values = Values;
        if (!Active || values is null || values.Length == 0)
        {
            dc.DrawLine(new Pen(IdleBrush, 1.5), new Point(0, graphHeight - 2), new Point(width, graphHeight - 2));
            var text = new FormattedText("Waiting for system audio", CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, LabelTypeface, 11, LabelBrush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(width / 2 - text.Width / 2, graphHeight / 2 - text.Height / 2));
            DrawLabels(dc, width, height, labelHeight);
            return;
        }

        if (HighlightBass && MaxFrequency > 250)
        {
            var start = LogPosition(20);
            var end = LogPosition(Math.Min(250, MaxFrequency));
            var region = new Rect(width * start, 0, Math.Max(1, width * (end - start)), graphHeight);
            var bassBrush = new LinearGradientBrush(
                Color.FromArgb(28, 139, 92, 246),
                Color.FromArgb(9, 139, 92, 246), 90);
            bassBrush.Freeze();
            dc.DrawRectangle(bassBrush, null, region);
        }

        var count = values.Length;
        var gap = count <= 32 ? 2.0 : 1.0;
        var barWidth = Math.Max(1.2, (width - gap * (count - 1)) / count);
        var peakIndex = 0;
        var peakValue = 0f;
        for (var i = 0; i < count; i++)
        {
            var value = Math.Clamp(values[i], 0, 1);
            if (value > peakValue)
            {
                peakValue = value;
                peakIndex = i;
            }
        }

        for (var i = 0; i < count; i++)
        {
            var raw = Math.Clamp(values[i], 0, 1);
            var eased = Math.Pow(raw, 0.8);
            var barHeight = Math.Max(1.5, eased * (graphHeight - 4));
            var x = i * (barWidth + gap);
            var rect = new Rect(x, graphHeight - barHeight, barWidth, barHeight);
            var opacity = 0.28 + 0.72 * eased;
            var top = Lighten(accentColor, i == peakIndex ? 0.35 : 0.1);
            var brush = new LinearGradientBrush(
                Color.FromArgb((byte)(255 * Math.Clamp(opacity, 0, 1)), accentColor.R, accentColor.G, accentColor.B),
                Color.FromArgb((byte)(70 * opacity), top.R, top.G, top.B), 90);
            brush.Freeze();
            dc.DrawRoundedRectangle(brush, null, rect, Math.Min(barWidth / 2, 2.4), Math.Min(barWidth / 2, 2.4));

            if (i == peakIndex && peakValue > 0.08)
            {
                dc.DrawRectangle(Freeze(new SolidColorBrush(Color.FromArgb(220, top.R, top.G, top.B))),
                    null, new Rect(x, Math.Max(0, graphHeight - barHeight - 2), barWidth, 1.6));
            }
        }

        DrawLabels(dc, width, height, labelHeight);
    }

    private void DrawLabels(DrawingContext dc, double width, double height, double labelHeight)
    {
        if (!ShowFrequencyLabels || labelHeight <= 0) return;

        var frequencies = new[] { 20d, 50, 100, 250, 500, 1000, 2000, 5000, 10000, 20000 };
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var frequency in frequencies)
        {
            if (frequency < MinFrequency || frequency > MaxFrequency) continue;
            var position = LogPosition(frequency);
            if (position < -0.01 || position > 1.01) continue;

            var label = frequency >= 1000
                ? $"{frequency / 1000:0.#}k"
                : $"{frequency:0}";
            var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                LabelTypeface, 9.5, LabelBrush, pixelsPerDip);
            var x = Math.Clamp(position * width - text.Width / 2, 0, width - text.Width);
            dc.DrawText(text, new Point(x, height - labelHeight + 3));
        }
    }

    private double LogPosition(double frequency)
    {
        var min = Math.Log10(Math.Max(1, MinFrequency));
        var max = Math.Log10(Math.Max(MinFrequency * 2, MaxFrequency));
        var position = (Math.Log10(Math.Max(1, frequency)) - min) / (max - min);
        return Math.Clamp(position, 0, 1);
    }

    private static Color Lighten(Color color, double amount) => Color.FromRgb(
        (byte)Math.Min(255, color.R + (255 - color.R) * amount),
        (byte)Math.Min(255, color.G + (255 - color.G) * amount),
        (byte)Math.Min(255, color.B + (255 - color.B) * amount));

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
