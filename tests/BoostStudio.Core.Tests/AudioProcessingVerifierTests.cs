using BoostStudio.Core.Audio;
using BoostStudio.Core.Engine;

namespace BoostStudio.Core.Tests;

public sealed class AudioProcessingVerifierTests
{
    private const int SampleRate = 48000;

    [Fact]
    public void MeasureToneLevelMatchesKnownAmplitude()
    {
        var samples = Sine(1000, 0.25, SampleRate);

        var toneDb = AudioProcessingVerifier.MeasureToneLevelDb(samples, SampleRate, 1000);
        var broadbandDb = AudioProcessingVerifier.MeasureBroadbandRmsDb(samples);
        var peakDb = AudioProcessingVerifier.MeasurePeakDb(samples);

        // A 0.25 amplitude sine has -15.05 dBFS RMS and -12.04 dBFS peak.
        Assert.InRange(toneDb, -15.10, -15.00);
        Assert.InRange(broadbandDb, -15.10, -15.00);
        Assert.InRange(peakDb, -12.08, -12.00);
    }

    [Fact]
    public void MeasureToneLevelRejectsOtherFrequenciesAndNoise()
    {
        var sine = Sine(100, 0.2, SampleRate);
        var wrongFrequency = AudioProcessingVerifier.MeasureToneLevelDb(sine, SampleRate, 1000);
        Assert.True(wrongFrequency < -60, $"A 100 Hz tone measured {wrongFrequency:0.0} dB at 1 kHz.");

        var random = new Random(1234);
        var noise = new float[SampleRate];
        for (var i = 0; i < noise.Length; i++)
            noise[i] = (float)((random.NextDouble() * 2 - 1) * 0.05);

        var noiseLevel = AudioProcessingVerifier.MeasureToneLevelDb(noise, SampleRate, 1000);
        Assert.True(noiseLevel < -60, $"White noise measured {noiseLevel:0.0} dB at 1 kHz.");
    }

    [Fact]
    public void AddPreampOffsetChangesExistingPreampWithoutAddingASecondLine()
    {
        var original = "# comment\nPreamp: -5.5 dB\nFilter 1: ON LS Fc 55 Hz Gain 11.0 dB Q 0.550\n";

        var attenuated = AudioEngineService.AddPreampOffset(original, -18.0);

        Assert.Contains("Preamp: -23.5 dB", attenuated, StringComparison.Ordinal);
        Assert.Single(attenuated.Split('\n').Where(line =>
            line.TrimStart().StartsWith("Preamp:", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains("Filter 1: ON LS", attenuated, StringComparison.Ordinal);
    }

    [Fact]
    public void AddPreampOffsetInsertsALineWhenTheConfigurationHasNone()
    {
        var original = "# header\nChannel: all\nFilter 1: ON LS Fc 55 Hz Gain 11.0 dB Q 0.550\n";

        var attenuated = AudioEngineService.AddPreampOffset(original, -18.0);

        var lines = attenuated.Split('\n');
        Assert.Equal("Preamp: -18.0 dB", lines[1].Trim());
        Assert.Contains("Filter 1: ON LS", attenuated, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigFingerprintIgnoresCommentsAndWhitespaceButDetectsDspChanges()
    {
        const string first = """
            # generated 2026-01-01 10:00:00
            Preamp: -5.5 dB
            Filter 1: ON LS Fc 55.0 Hz Gain 11.0 dB Q 0.550
            """;
        const string sameCommands = """

            # generated 2026-02-02 11:30:00
              Preamp: -5.5 dB
            Filter 1: ON LS Fc 55.0 Hz Gain 11.0 dB Q 0.550
            """;
        const string changedGain = """
            # generated 2026-01-01 10:00:00
            Preamp: -5.5 dB
            Filter 1: ON LS Fc 55.0 Hz Gain 9.0 dB Q 0.550
            """;

        var baseline = AudioEngineService.ComputeConfigFingerprint(first);

        Assert.Equal(baseline, AudioEngineService.ComputeConfigFingerprint(sameCommands));
        Assert.NotEqual(baseline, AudioEngineService.ComputeConfigFingerprint(changedGain));
    }

    private static float[] Sine(double frequencyHz, double amplitude, int sampleRate)
    {
        var samples = new float[sampleRate];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (float)(Math.Sin(2 * Math.PI * frequencyHz * i / sampleRate) * amplitude);
        return samples;
    }
}
