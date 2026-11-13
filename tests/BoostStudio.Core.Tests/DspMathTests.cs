using BoostStudio.Core.Dsp;
using BoostStudio.Core.Models;

namespace BoostStudio.Core.Tests;

public sealed class DspMathTests
{
    private const double SampleRate = 48000;

    [Theory]
    [InlineData(1000d, 6d, 1.41)]
    [InlineData(250d, -9d, 1.41)]
    [InlineData(16000d, 3d, 0.8)]
    public void PeakingFilterHasRequestedGainAtCenter(double frequency, double gain, double q)
    {
        var filter = DspMath.Peaking(frequency, gain, q, SampleRate);
        var measured = filter.GainDbAt(frequency, SampleRate);

        Assert.Equal(gain, measured, 2);
        Assert.Equal("PK", filter.Kind);
    }

    [Fact]
    public void PeakingFilterRollsOffAwayFromCenter()
    {
        var filter = DspMath.Peaking(1000, 9, 1.41, SampleRate);

        Assert.InRange(Math.Abs(filter.GainDbAt(40, SampleRate)), 0, 0.5);
        Assert.InRange(Math.Abs(filter.GainDbAt(20000, SampleRate)), 0, 0.5);
    }

    [Fact]
    public void ProtectHeadroomKeepsEveryFrequencyBelowUnity()
    {
        var settings = AppSettings.CreateDefault();
        settings.AutoHeadroom = true;
        settings.Bass.Enabled = true;
        settings.Bass.Intensity = 100;
        settings.Bass.MaxGainDb = 24;
        settings.Bass.FrequencyHz = 45;
        settings.Bass.ShelfQ = 0.55;
        settings.Bass.ProtectHeadroom = true;
        settings.Volume.Enabled = false;
        settings.Volume.Loudness = 0;
        settings.Equalizer.Enabled = true;
        settings.Equalizer.GainsDb = Enumerable.Repeat(12.0, EqConstants.BandCount).ToArray();
        settings.Normalize();

        var staticBoost = DspMath.ComputeMaxStaticBoostDb(settings);
        var reserved = DspMath.ComputeHeadroomReservationDb(settings);
        var filters = DspMath.BuildStaticFilters(settings);

        // Safe mode must cover the measured peak boost plus the safety margin;
        // reserving only part of the boost leaves a positive net gain and can clip.
        Assert.True(reserved >= Math.Round(staticBoost + 0.5, 1) - 0.001,
            $"Reserved {reserved:0.0} dB for a measured {staticBoost:0.0} dB boost.");

        for (var frequency = 20.0; frequency <= 20000; frequency *= 1.01)
        {
            var netGain = DspMath.EvaluateResponseDb(filters, frequency) - reserved;
            Assert.True(netGain <= 0.01,
                $"Net gain at {frequency:0} Hz is {netGain:+0.00;-0.00;0.00} dB with safe headroom.");
        }
    }

    [Fact]
    public void DefaultHeadroomExcludesBassShelf()
    {
        var settings = TestSettings.BassOnly();
        settings.AutoHeadroom = true;

        Assert.Equal(0, DspMath.ComputeHeadroomReservationDb(settings), 3);
        Assert.Equal(0, DspMath.ComputeHeadroomBoostDb(settings), 3);
        Assert.True(DspMath.ComputeMaxStaticBoostDb(settings) > 8,
            "The bass shelf itself should still be a meaningful boost.");
    }

    [Fact]
    public void ProtectHeadroomIncludesBassShelf()
    {
        var settings = TestSettings.BassOnly();
        settings.AutoHeadroom = true;
        settings.Bass.ProtectHeadroom = true;

        var headroom = DspMath.ComputeHeadroomReservationDb(settings);

        Assert.True(headroom > 8, $"Expected the shelf gain to be reserved, got {headroom} dB.");
        Assert.Equal(Math.Round(DspMath.ComputeMaxStaticBoostDb(settings) + 0.5, 1), headroom, 3);
    }

    [Fact]
    public void ZeroGainIsUnity()
    {
        var filter = DspMath.Peaking(1000, 0, 1.41, SampleRate);
        Assert.Equal(1, filter.B0, 6);
        Assert.Equal(filter.A1, filter.B1, 6);
        Assert.Equal(filter.A2, filter.B2, 6);
        Assert.Equal(0, filter.GainDbAt(1000, SampleRate), 4);
        Assert.Equal(0, filter.GainDbAt(50, SampleRate), 4);
    }

    [Theory]
    [InlineData(80d, 12d, 0.707)]
    [InlineData(45d, 6d, 0.4)]
    [InlineData(180d, 18d, 1.2)]
    public void LowShelfBoostsLowFrequenciesAndIsFlatAtTop(double frequency, double gain, double q)
    {
        var filter = DspMath.LowShelf(frequency, gain, q, SampleRate);

        Assert.InRange(filter.GainDbAt(1, SampleRate), gain - 1.0, gain + 1.0);
        Assert.InRange(Math.Abs(filter.GainDbAt(20000, SampleRate)), 0, 3.0);
    }

    [Fact]
    public void HighShelfBoostsHighFrequenciesAndIsFlatAtBottom()
    {
        var filter = DspMath.HighShelf(9000, 6, 0.707, SampleRate);

        Assert.InRange(filter.GainDbAt(20000, SampleRate), 5.0, 7.0);
        Assert.InRange(Math.Abs(filter.GainDbAt(30, SampleRate)), 0, 1.5);
    }

    [Fact]
    public void ZeroGainShelvesAreUnity()
    {
        var low = DspMath.LowShelf(80, 0, 0.707, SampleRate);
        var high = DspMath.HighShelf(9000, 0, 0.707, SampleRate);

        Assert.Equal(0, low.GainDbAt(20, SampleRate), 3);
        Assert.Equal(0, high.GainDbAt(20000, SampleRate), 3);
        Assert.Equal("LS", low.Kind);
        Assert.Equal("HS", high.Kind);
    }

    [Fact]
    public void BassFiltersRespectEnabledAndIntensity()
    {
        var bass = new BassSettings { Enabled = false, Intensity = 100, MaxGainDb = 18 };
        Assert.Empty(DspMath.BuildBassFilters(bass, SampleRate));

        bass.Enabled = true;
        bass.Intensity = 0;
        Assert.Empty(DspMath.BuildBassFilters(bass, SampleRate));

        bass.Intensity = 50;
        var filters = DspMath.BuildBassFilters(bass, SampleRate);
        Assert.Single(filters);
        Assert.Equal("LS", filters[0].Kind);
        Assert.Equal(9, bass.EffectiveGainDb, 2);
    }

    [Fact]
    public void EqualizerBuildsOnlyActiveBands()
    {
        var gains = new double[EqConstants.BandCount];
        gains[0] = 3;
        gains[4] = -2;
        gains[9] = 0.01;

        var filters = DspMath.BuildEqFilters(gains, SampleRate);

        Assert.Equal(2, filters.Length);
        Assert.All(filters, filter => Assert.Equal("PK", filter.Kind));
        Assert.Equal(3, filters[0].GainDbAt(EqConstants.BandFrequencies[0], SampleRate), 1);
        Assert.Equal(-2, filters[1].GainDbAt(EqConstants.BandFrequencies[4], SampleRate), 1);
    }

    [Fact]
    public void LoudnessFiltersFollowVolumeState()
    {
        var volume = new VolumeSettings { Enabled = false, Loudness = 100 };
        Assert.Empty(DspMath.BuildLoudnessFilters(volume, SampleRate));

        volume.Enabled = true;
        volume.Loudness = 0;
        Assert.Empty(DspMath.BuildLoudnessFilters(volume, SampleRate));

        volume.Loudness = 50;
        var filters = DspMath.BuildLoudnessFilters(volume, SampleRate);
        Assert.Equal(2, filters.Length);
        Assert.Contains(filters, f => f.Kind == "LS");
        Assert.Contains(filters, f => f.Kind == "HS");
    }

    [Fact]
    public void MasterBypassRemovesAllStaticFilters()
    {
        var settings = TestSettings.BassOnly();
        settings.Equalizer.Enabled = true;
        settings.Equalizer.GainsDb[3] = 6;
        settings.ProcessingEnabled = false;

        Assert.Empty(DspMath.BuildStaticFilters(settings));
        Assert.Equal(0, DspMath.ComputeMaxStaticBoostDb(settings));
        Assert.Equal(0, DspMath.ComputeHeadroomReservationDb(settings));
    }

    [Fact]
    public void MaxBoostAndHeadroomReserveSafetyMarginForManagedFilters()
    {
        var settings = TestSettings.EqualizerOnly();
        settings.AutoHeadroom = true;
        settings.Equalizer.GainsDb = new double[] { 0, 6, 0, 0, 0, 0, 0, 0, 0, 0 };
        settings.Volume.Loudness = 100;
        settings.Bass.Enabled = true;
        settings.Bass.Intensity = 100;
        settings.Bass.MaxGainDb = 12;

        var maxBoost = DspMath.ComputeMaxStaticBoostDb(settings);
        var managedBoost = DspMath.ComputeHeadroomBoostDb(settings);
        var headroom = DspMath.ComputeHeadroomReservationDb(settings);

        Assert.True(maxBoost > 10, $"Expected a meaningful combined boost, got {maxBoost} dB.");
        Assert.True(managedBoost > 0, "EQ and loudness should still reserve headroom.");
        Assert.True(managedBoost < maxBoost,
            "The bass shelf must not be part of the default headroom reservation.");
        Assert.InRange(headroom, managedBoost + 0.4, managedBoost + 0.6);
        Assert.Equal(Math.Round(managedBoost + 0.5, 1), headroom, 3);
    }

    [Fact]
    public void AutoHeadroomCanBeDisabled()
    {
        var settings = TestSettings.BassOnly();
        settings.AutoHeadroom = false;

        Assert.Equal(0, DspMath.ComputeHeadroomReservationDb(settings));
    }

    [Fact]
    public void EvaluateHelpersSumFilters()
    {
        var settings = TestSettings.BassOnly();
        var filters = DspMath.BuildStaticFilters(settings);
        var frequency = 60d;

        var expected = filters.Sum(f => f.GainDbAt(frequency, SampleRate));
        Assert.Equal(expected, DspMath.EvaluateResponseDb(filters, frequency, SampleRate), 6);
        Assert.Equal(
            DspMath.EvaluateBassResponseDb(settings.Bass, frequency, SampleRate),
            DspMath.EvaluateResponseDb(filters, frequency, SampleRate), 6);
    }

    [Fact]
    public void BassShelfIsFlatAtOneKhzAndBoostsLowBass()
    {
        var settings = TestSettings.BassOnly();
        var filters = DspMath.BuildBassFilters(settings.Bass);

        Assert.InRange(Math.Abs(DspMath.EvaluateResponseDb(filters, 1000, SampleRate)), 0, 0.1);
        Assert.True(DspMath.EvaluateResponseDb(filters, 50, SampleRate) > 6,
            "50 Hz should receive most of the shelf gain.");
        Assert.True(DspMath.EvaluateResponseDb(filters, 100, SampleRate) > 4,
            "100 Hz should also receive a clear boost.");
    }

    [Fact]
    public void FormatDbUsesInvariantCulture()
    {
        Assert.Equal("+3.5 dB", DspMath.FormatDb(3.5));
        Assert.Equal("-2.0 dB", DspMath.FormatDb(-2));
        Assert.Equal("0.0 dB", DspMath.FormatDb(0));
        Assert.Equal("12.0 dB", DspMath.FormatDb(12, sign: false));
    }
}
