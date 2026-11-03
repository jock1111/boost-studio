using BoostStudio.Core.Dsp;

namespace BoostStudio.Core.Tests;

public sealed class FftAndSpectrumTests
{
    [Fact]
    public void FftOfDcSignalConcentratesAtBinZero()
    {
        var real = Enumerable.Repeat(1d, 16).ToArray();
        var imaginary = new double[16];

        Fft.Transform(real, imaginary);

        Assert.Equal(16, real[0], 6);
        Assert.Equal(0, imaginary[0], 6);
        for (var i = 1; i < 16; i++)
        {
            Assert.Equal(0, real[i], 6);
            Assert.Equal(0, imaginary[i], 6);
        }
    }

    [Fact]
    public void FftOfCosineProducesConjugatePeaks()
    {
        const int n = 1024;
        const int bin = 64;
        var real = new double[n];
        var imaginary = new double[n];
        for (var i = 0; i < n; i++)
            real[i] = Math.Cos(2 * Math.PI * bin * i / n);

        Fft.Transform(real, imaginary);

        Assert.Equal(n / 2.0, real[bin], 5);
        Assert.Equal(0, imaginary[bin], 5);
        Assert.Equal(n / 2.0, real[n - bin], 5);
        Assert.Equal(0, imaginary[n - bin], 5);
        Assert.Equal(0, real[bin + 1], 5);
    }

    [Fact]
    public void FftValidatesBufferShapes()
    {
        Assert.True(Fft.IsPowerOfTwo(1));
        Assert.True(Fft.IsPowerOfTwo(8192));
        Assert.False(Fft.IsPowerOfTwo(0));
        Assert.False(Fft.IsPowerOfTwo(1000));

        Assert.Throws<ArgumentException>(() => Fft.Transform(new double[8], new double[7]));
        Assert.Throws<ArgumentException>(() => Fft.Transform(new double[10], new double[10]));
        Fft.Transform(Array.Empty<double>(), Array.Empty<double>());
    }

    [Fact]
    public void AnalyzerFindsBassFundamental()
    {
        const double sampleRate = 48000;
        const double frequency = 80;
        var samples = new float[SpectrumAnalyzer.FftSize];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (float)(0.5 * Math.Sin(2 * Math.PI * frequency * i / sampleRate));

        var analyzer = new SpectrumAnalyzer(sampleRate);
        var result = analyzer.Analyze(samples);

        Assert.InRange(result.DominantBassHz, 70, 90);
        Assert.InRange(result.RmsDb, -11, -8);
        Assert.InRange(result.PeakDb, -7, -5.5);
        Assert.Equal(0, result.ClippingSamples);
        Assert.Equal(SpectrumAnalyzer.SpectrumBands, result.Spectrum.Length);
        Assert.Equal(SpectrumAnalyzer.BassBands, result.Bass.Length);
        Assert.All(result.Spectrum, value => Assert.InRange(value, 0f, 1f));
        Assert.All(result.Bass, value => Assert.InRange(value, 0f, 1f));
        Assert.True(result.Bass.Max() > 0.4f);
    }

    [Fact]
    public void AnalyzerCountsNearFullScaleSamples()
    {
        var samples = new float[4096];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (float)Math.Cos(2 * Math.PI * 1000 * i / 48000);
        samples[100] = 1f;
        samples[200] = -1f;

        var analyzer = new SpectrumAnalyzer(48000);
        var result = analyzer.Analyze(samples);

        Assert.True(result.ClippingSamples >= 2);
    }

    [Fact]
    public void AnalyzerHandlesSilenceAndShortBuffers()
    {
        var analyzer = new SpectrumAnalyzer(44100);
        var silence = analyzer.Analyze(new float[SpectrumAnalyzer.FftSize]);
        Assert.Equal(-120, silence.RmsDb, 3);
        Assert.Equal(-120, silence.PeakDb, 3);
        Assert.Equal(0, silence.DominantBassHz, 3);
        Assert.All(silence.Spectrum, value => Assert.Equal(0f, value, 6));

        var shortBuffer = new float[512];
        for (var i = 0; i < shortBuffer.Length; i++)
            shortBuffer[i] = (float)(0.2 * Math.Sin(2 * Math.PI * 500 * i / 44100));

        var shortResult = analyzer.Analyze(shortBuffer);
        Assert.True(double.IsFinite(shortResult.RmsDb));
        Assert.True(shortResult.RmsDb <= 0);
        Assert.All(shortResult.Spectrum, value => Assert.InRange(value, 0f, 1f));
    }

    [Fact]
    public void AnalyzerClampsUnusableSampleRates()
    {
        var analyzer = new SpectrumAnalyzer(100);
        Assert.Equal(48000, analyzer.SampleRate);
    }

    [Theory]
    [InlineData(20, 20, 20000, 0)]
    [InlineData(20000, 20, 20000, 1)]
    [InlineData(200, 20, 20000, 0.333)]
    public void LogPositionMapsFrequencyAxis(double frequency, double min, double max, double expected)
    {
        Assert.Equal(expected, SpectrumAnalyzer.LogPosition(frequency, min, max), 2);
    }
}
