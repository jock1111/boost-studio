using BoostStudio.Core.Models;

namespace BoostStudio.Core.Dsp;

/// <summary>
/// Converts an 8192-sample window into log-spaced display bands. The analyzer is
/// purely a measurement component; it is not part of the audio output path.
/// </summary>
public sealed class SpectrumAnalyzer
{
    public const int FftSize = 8192;
    public const int SpectrumBands = 56;
    public const int BassBands = 28;
    public const double MinDisplayDb = -84.0;

    private readonly double[] _window = new double[FftSize];
    private readonly double[] _real = new double[FftSize];
    private readonly double[] _imag = new double[FftSize];
    private readonly double[] _magnitudes = new double[FftSize / 2];
    private readonly double _sampleRate;
    private readonly float[] _spectrum = new float[SpectrumBands];
    private readonly float[] _bass = new float[BassBands];

    public double SampleRate => _sampleRate;

    public SpectrumAnalyzer(double sampleRate)
    {
        _sampleRate = sampleRate > 1000 ? sampleRate : 48000;
        for (var i = 0; i < FftSize; i++)
        {
            // Hann window; coherent gain is 0.5.
            _window[i] = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (FftSize - 1)));
        }
    }

    public (float[] Spectrum, float[] Bass, double DominantBassHz, double RmsDb, double PeakDb, long ClippingSamples)
        Analyze(ReadOnlySpan<float> samples)
    {
        var count = Math.Min(FftSize, samples.Length);
        var start = samples.Length - count;
        var sumSquares = 0.0;
        var peak = 0.0;

        for (var i = 0; i < FftSize; i++)
        {
            var sample = i < count ? (double)samples[start + i] : 0;
            var magnitude = Math.Abs(sample);
            if (magnitude > peak) peak = magnitude;
            sumSquares += sample * sample;
            var window = count > 1
                ? 0.5 * (1 - Math.Cos(2 * Math.PI * i / (count - 1)))
                : 1.0;
            _real[i] = sample * window;
            _imag[i] = 0;
        }

        var rms = count > 0 ? Math.Sqrt(sumSquares / count) : 0;
        Fft.Transform(_real, _imag);

        var binHz = _sampleRate / FftSize;
        for (var i = 0; i < FftSize / 2; i++)
        {
            var amplitude = 4.0 * Math.Sqrt(_real[i] * _real[i] + _imag[i] * _imag[i]) / FftSize;
            var db = 20 * Math.Log10(amplitude + 1e-15);
            _magnitudes[i] = db;
        }

        FillLogBands(_spectrum, 20, Math.Min(20000, _sampleRate / 2), binHz);
        FillLogBands(_bass, 20, 250, binHz);

        var dominantBass = FindDominantFrequency(20, 250, binHz);
        var clipping = 0L;
        for (var i = 0; i < count; i++)
        {
            if (Math.Abs(samples[start + i]) >= 0.9995f)
                clipping++;
        }

        return (_spectrum, _bass, dominantBass, ToDb(rms), ToDb(peak), clipping);
    }

    private void FillLogBands(float[] destination, double minFrequency, double maxFrequency, double binHz)
    {
        var count = destination.Length;
        var logMin = Math.Log10(minFrequency);
        var logMax = Math.Log10(maxFrequency);
        var maxBin = _magnitudes.Length - 1;

        for (var band = 0; band < count; band++)
        {
            var f0 = Math.Pow(10, logMin + (logMax - logMin) * band / count);
            var f1 = Math.Pow(10, logMin + (logMax - logMin) * (band + 1) / count);
            var bin0 = Math.Max(1, (int)Math.Floor(f0 / binHz));
            var bin1 = Math.Min(maxBin, Math.Max(bin0, (int)Math.Ceiling(f1 / binHz)));

            var maxDb = -160.0;
            for (var bin = bin0; bin <= bin1; bin++)
                maxDb = Math.Max(maxDb, _magnitudes[bin]);

            // If the band is narrower than one bin, sample the nearest bin.
            if (bin1 <= bin0)
            {
                var nearest = Math.Clamp((int)Math.Round(Math.Sqrt(f0 * f1) / binHz), 1, maxBin);
                maxDb = _magnitudes[nearest];
            }

            var normalized = Math.Clamp((maxDb - MinDisplayDb) / -MinDisplayDb, 0, 1);
            destination[band] = (float)Math.Pow(normalized, 0.85);
        }
    }

    private double FindDominantFrequency(double minFrequency, double maxFrequency, double binHz)
    {
        var minBin = Math.Max(1, (int)Math.Floor(minFrequency / binHz));
        var maxBin = Math.Min(_magnitudes.Length - 1, (int)Math.Ceiling(maxFrequency / binHz));
        var bestDb = -90.0;
        var bestBin = 0;

        for (var bin = minBin; bin <= maxBin; bin++)
        {
            if (_magnitudes[bin] > bestDb)
            {
                bestDb = _magnitudes[bin];
                bestBin = bin;
            }
        }

        return bestDb > -70 ? bestBin * binHz : 0;
    }

    private static double ToDb(double linear) =>
        linear > 1e-9 ? 20 * Math.Log10(linear) : -120;

    /// <summary>Log-frequency position (0-1) for a chart axis.</summary>
    public static double LogPosition(double frequency, double minFrequency, double maxFrequency) =>
        (Math.Log10(Math.Clamp(frequency, minFrequency, maxFrequency)) - Math.Log10(minFrequency)) /
        (Math.Log10(maxFrequency) - Math.Log10(minFrequency));
}
