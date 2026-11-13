using System.Globalization;
using BoostStudio.Core.Models;

namespace BoostStudio.Core.Dsp;

/// <summary>
/// A normalized biquad transfer function. Coefficients follow Equalizer APO's own
/// BiQuad implementation so the graph shown in the UI matches the audio engine.
/// </summary>
public readonly record struct Biquad(double B0, double B1, double B2, double A1, double A2, string Kind)
{
    public double GainDbAt(double frequencyHz, double sampleRate)
    {
        if (frequencyHz <= 0 || sampleRate <= 0)
            return 0;

        var omega = 2 * Math.PI * frequencyHz / sampleRate;
        var cos1 = Math.Cos(omega);
        var sin1 = Math.Sin(omega);
        var cos2 = Math.Cos(2 * omega);
        var sin2 = Math.Sin(2 * omega);

        var numeratorReal = B0 + B1 * cos1 + B2 * cos2;
        var numeratorImag = -(B1 * sin1 + B2 * sin2);
        var denominatorReal = 1 + A1 * cos1 + A2 * cos2;
        var denominatorImag = -(A1 * sin1 + A2 * sin2);

        var numeratorPower = numeratorReal * numeratorReal + numeratorImag * numeratorImag;
        var denominatorPower = denominatorReal * denominatorReal + denominatorImag * denominatorImag;
        if (numeratorPower <= double.Epsilon || denominatorPower <= double.Epsilon)
            return -120;

        var gain = 10 * Math.Log10(numeratorPower / denominatorPower);
        return double.IsFinite(gain) ? gain : 0;
    }
}

public static class DspMath
{
    public const int DefaultSampleRate = 48000;
    private const double SafetyMarginDb = 0.5;

    /// <summary>Peaking EQ using the same cookbook equations as Equalizer APO.</summary>
    public static Biquad Peaking(double frequencyHz, double gainDb, double q, double sampleRate)
    {
        var a = Math.Pow(10, gainDb / 40.0);
        var omega = 2 * Math.PI * frequencyHz / sampleRate;
        var sn = Math.Sin(omega);
        var cs = Math.Cos(omega);
        var alpha = sn / (2 * Math.Max(0.0001, q));

        var b0 = 1 + alpha * a;
        var b1 = -2 * cs;
        var b2 = 1 - alpha * a;
        var a0 = 1 + alpha / a;
        var a1 = -2 * cs;
        var a2 = 1 - alpha / a;

        return new Biquad(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0, "PK");
    }

    /// <summary>
    /// Low shelf matching Equalizer APO's <c>LS</c> filter with a Q parameter.
    /// Equalizer APO converts Q to a shelf slope and shifts the actual center
    /// frequency for corner-frequency low shelves; graphics account for that shift.
    /// </summary>
    public static Biquad LowShelf(double frequencyHz, double gainDb, double q, double sampleRate)
    {
        if (Math.Abs(gainDb) < 0.0001)
            return new Biquad(1, 0, 0, 0, 0, "LS");

        var a = Math.Pow(10, gainDb / 40.0);
        var actualQ = Math.Max(0.05, q);
        var slope = 1.0 / ((1.0 / (actualQ * actualQ) - 2.0) / (a + 1.0 / a) + 1.0);
        slope = Math.Clamp(slope, 0.0001, 10.0);
        var centerFrequencyFactor = Math.Pow(10.0, Math.Abs(gainDb) / 80.0 / slope);
        var biquadFrequency = frequencyHz * centerFrequencyFactor;

        var omega = 2 * Math.PI * biquadFrequency / sampleRate;
        var sn = Math.Sin(omega);
        var cs = Math.Cos(omega);
        var alpha = sn / 2 * Math.Sqrt((a + 1 / a) * (1 / slope - 1) + 2);
        var beta = 2 * Math.Sqrt(a) * alpha;

        var b0 = a * ((a + 1) - (a - 1) * cs + beta);
        var b1 = 2 * a * ((a - 1) - (a + 1) * cs);
        var b2 = a * ((a + 1) - (a - 1) * cs - beta);
        var a0 = (a + 1) + (a - 1) * cs + beta;
        var a1 = -2 * ((a - 1) + (a + 1) * cs);
        var a2 = (a + 1) + (a - 1) * cs - beta;

        return new Biquad(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0, "LS");
    }

    /// <summary>High shelf matching Equalizer APO's HS filter with a Q parameter.</summary>
    public static Biquad HighShelf(double frequencyHz, double gainDb, double q, double sampleRate)
    {
        if (Math.Abs(gainDb) < 0.0001)
            return new Biquad(1, 0, 0, 0, 0, "HS");

        var a = Math.Pow(10, gainDb / 40.0);
        var actualQ = Math.Max(0.05, q);
        var slope = 1.0 / ((1.0 / (actualQ * actualQ) - 2.0) / (a + 1.0 / a) + 1.0);
        slope = Math.Clamp(slope, 0.0001, 10.0);
        var centerFrequencyFactor = Math.Pow(10.0, Math.Abs(gainDb) / 80.0 / slope);
        var biquadFrequency = frequencyHz / centerFrequencyFactor;

        var omega = 2 * Math.PI * biquadFrequency / sampleRate;
        var sn = Math.Sin(omega);
        var cs = Math.Cos(omega);
        var alpha = sn / 2 * Math.Sqrt((a + 1 / a) * (1 / slope - 1) + 2);
        var beta = 2 * Math.Sqrt(a) * alpha;

        var b0 = a * ((a + 1) + (a - 1) * cs + beta);
        var b1 = -2 * a * ((a - 1) + (a + 1) * cs);
        var b2 = a * ((a + 1) + (a - 1) * cs - beta);
        var a0 = (a + 1) - (a - 1) * cs + beta;
        var a1 = 2 * ((a - 1) - (a + 1) * cs);
        var a2 = (a + 1) - (a - 1) * cs - beta;

        return new Biquad(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0, "HS");
    }

    public static Biquad[] BuildBassFilters(BassSettings bass, double sampleRate = DefaultSampleRate)
    {
        if (!bass.Enabled || bass.EffectiveGainDb < 0.01)
            return Array.Empty<Biquad>();

        return new[] { LowShelf(bass.FrequencyHz, bass.EffectiveGainDb, bass.ShelfQ, sampleRate) };
    }

    public static Biquad[] BuildEqFilters(double[] gains, double sampleRate = DefaultSampleRate)
    {
        var filters = new List<Biquad>(EqConstants.BandCount);
        for (var i = 0; i < EqConstants.BandCount && i < gains.Length; i++)
        {
            var gain = gains[i];
            if (Math.Abs(gain) < 0.05)
                continue;
            filters.Add(Peaking(EqConstants.BandFrequencies[i], gain, EqConstants.Q, sampleRate));
        }

        return filters.ToArray();
    }

    public static Biquad[] BuildLoudnessFilters(VolumeSettings volume, double sampleRate = DefaultSampleRate)
    {
        if (!volume.Enabled || volume.Loudness <= 0)
            return Array.Empty<Biquad>();

        var scale = volume.Loudness / 100.0;
        var lowGain = 4.0 * scale;
        var highGain = 2.5 * scale;
        var filters = new List<Biquad>(2);
        if (lowGain > 0.01) filters.Add(LowShelf(110, lowGain, 0.707, sampleRate));
        if (highGain > 0.01) filters.Add(HighShelf(9000, highGain, 0.707, sampleRate));
        return filters.ToArray();
    }

    /// <summary>All enabled static filters (bass, EQ, loudness), excluding the volume preamp.</summary>
    public static Biquad[] BuildStaticFilters(AppSettings settings, double sampleRate = DefaultSampleRate)
    {
        var list = new List<Biquad>(16);
        if (settings.ProcessingEnabled)
        {
            list.AddRange(BuildBassFilters(settings.Bass, sampleRate));
            if (settings.Equalizer.Enabled)
                list.AddRange(BuildEqFilters(settings.Equalizer.GainsDb, sampleRate));
            list.AddRange(BuildLoudnessFilters(settings.Volume, sampleRate));
        }

        return list.ToArray();
    }

    /// <summary>
    /// Filters that auto headroom compensates. Bass is excluded by default so turning
    /// Bass Booster on never lowers the rest of the spectrum; the optional safe mode
    /// (<see cref="BassSettings.ProtectHeadroom"/>) adds it back.
    /// </summary>
    public static Biquad[] BuildHeadroomFilters(AppSettings settings, double sampleRate = DefaultSampleRate)
    {
        var list = new List<Biquad>(16);
        if (!settings.ProcessingEnabled)
            return Array.Empty<Biquad>();

        if (settings.Bass.ProtectHeadroom)
            list.AddRange(BuildBassFilters(settings.Bass, sampleRate));
        if (settings.Equalizer.Enabled)
            list.AddRange(BuildEqFilters(settings.Equalizer.GainsDb, sampleRate));
        list.AddRange(BuildLoudnessFilters(settings.Volume, sampleRate));
        return list.ToArray();
    }

    public static double EvaluateResponseDb(IEnumerable<Biquad> filters, double frequencyHz, double sampleRate = DefaultSampleRate)
    {
        var total = 0.0;
        foreach (var filter in filters)
            total += filter.GainDbAt(frequencyHz, sampleRate);
        return total;
    }

    public static double EvaluateBassResponseDb(BassSettings bass, double frequencyHz, double sampleRate = DefaultSampleRate)
    {
        if (!bass.Enabled) return 0;
        return LowShelf(bass.FrequencyHz, bass.EffectiveGainDb, bass.ShelfQ, sampleRate)
            .GainDbAt(frequencyHz, sampleRate);
    }

    public static double EvaluateEqResponseDb(double[] gains, double frequencyHz, double sampleRate = DefaultSampleRate)
    {
        var total = 0.0;
        for (var i = 0; i < EqConstants.BandCount && i < gains.Length; i++)
        {
            if (Math.Abs(gains[i]) < 0.05) continue;
            total += Peaking(EqConstants.BandFrequencies[i], gains[i], EqConstants.Q, sampleRate)
                .GainDbAt(frequencyHz, sampleRate);
        }

        return total;
    }

    /// <summary>
    /// Measures the maximum combined boost of all static filters across the audio band.
    /// This is the total gain the filters can apply and is displayed for diagnostics; it
    /// is not the amount auto headroom reserves (bass is usually excluded there).
    /// </summary>
    public static double ComputeMaxStaticBoostDb(AppSettings settings, double sampleRate = DefaultSampleRate)
        => ComputeMaxBoostDb(BuildStaticFilters(settings, sampleRate), sampleRate);

    /// <summary>
    /// Maximum boost of the filters auto headroom is responsible for. By default this
    /// excludes the bass shelf, so enabling Bass Booster does not lower mids and highs.
    /// </summary>
    public static double ComputeHeadroomBoostDb(AppSettings settings, double sampleRate = DefaultSampleRate)
        => ComputeMaxBoostDb(BuildHeadroomFilters(settings, sampleRate), sampleRate);

    public static double ComputeHeadroomReservationDb(AppSettings settings, double sampleRate = DefaultSampleRate)
    {
        if (!settings.AutoHeadroom)
            return 0;

        var maxBoost = ComputeHeadroomBoostDb(settings, sampleRate);
        if (maxBoost <= 0.01)
            return 0;

        // Reserve the full measured boost of the headroom-managed filters (EQ and
        // loudness, plus bass in optional safe mode) and a small safety margin. This
        // keeps those filters from clipping without a permanent broadband cut just for
        // enabling bass. The bass shelf is intentionally excluded in the default mode:
        // its gain is ~0 dB at 1 kHz, so mids and highs stay at their original level.
        // Very loud, near-full-scale bass content can still clip in that mode; the UI
        // shows a clipping warning and Protect headroom is the static-guarantee option.
        return Math.Round(maxBoost + SafetyMarginDb, 1);
    }

    private static double ComputeMaxBoostDb(IReadOnlyList<Biquad> filters, double sampleRate)
    {
        if (filters.Count == 0) return 0;

        var maximum = 0.0;
        const int points = 900;
        var min = Math.Log10(2.0);
        var max = Math.Log10(Math.Min(22000.0, sampleRate / 2.0));
        for (var i = 0; i < points; i++)
        {
            var frequency = Math.Pow(10, min + (max - min) * i / (points - 1));
            maximum = Math.Max(maximum, EvaluateResponseDb(filters, frequency, sampleRate));
        }

        return maximum;
    }

    public static string FormatDb(double value, bool sign = true)
    {
        var format = sign ? "+0.0;-0.0;0.0" : "0.0";
        return value.ToString(format, CultureInfo.InvariantCulture) + " dB";
    }
}
