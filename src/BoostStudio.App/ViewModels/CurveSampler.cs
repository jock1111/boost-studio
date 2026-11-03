using BoostStudio.Core.Dsp;
using BoostStudio.Core.Models;

namespace BoostStudio.App.ViewModels;

/// <summary>Builds graph data from the same biquad math used to generate the APO file.</summary>
internal static class CurveSampler
{
    public static double[] Sample(double minFrequency, double maxFrequency, int points, Func<double, double> response)
    {
        points = Math.Clamp(points, 24, 600);
        var values = new double[points];
        var logMin = Math.Log10(Math.Max(1, minFrequency));
        var logMax = Math.Log10(Math.Max(minFrequency * 1.01, maxFrequency));
        for (var i = 0; i < points; i++)
        {
            var frequency = Math.Pow(10, logMin + (logMax - logMin) * i / (points - 1));
            var value = response(frequency);
            values[i] = double.IsFinite(value) ? Math.Round(value, 3) : 0;
        }

        return values;
    }

    public static double[] Bass(BassSettings bass, double minFrequency = 20, double maxFrequency = 320, int points = 190)
    {
        var filter = DspMath.LowShelf(bass.FrequencyHz, bass.EffectiveGainDb, bass.ShelfQ, DspMath.DefaultSampleRate);
        return Sample(minFrequency, maxFrequency, points,
            frequency => filter.GainDbAt(frequency, DspMath.DefaultSampleRate));
    }

    public static double[] Equalizer(double[] gains, double minFrequency = 20, double maxFrequency = 20000, int points = 240)
    {
        var filters = DspMath.BuildEqFilters(gains, DspMath.DefaultSampleRate);
        return Sample(minFrequency, maxFrequency, points,
            frequency => DspMath.EvaluateResponseDb(filters, frequency, DspMath.DefaultSampleRate));
    }

    public static double[] Loudness(VolumeSettings volume, double minFrequency = 20, double maxFrequency = 20000, int points = 220)
    {
        var filters = DspMath.BuildLoudnessFilters(volume, DspMath.DefaultSampleRate);
        return Sample(minFrequency, maxFrequency, points,
            frequency => DspMath.EvaluateResponseDb(filters, frequency, DspMath.DefaultSampleRate));
    }

    public static double[] CombinedStatic(AppSettings settings, double minFrequency = 20, double maxFrequency = 20000, int points = 240)
    {
        var filters = DspMath.BuildStaticFilters(settings, DspMath.DefaultSampleRate);
        return Sample(minFrequency, maxFrequency, points,
            frequency => DspMath.EvaluateResponseDb(filters, frequency, DspMath.DefaultSampleRate));
    }
}
