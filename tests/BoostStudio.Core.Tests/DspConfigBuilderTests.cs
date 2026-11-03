using BoostStudio.Core.Dsp;
using BoostStudio.Core.Models;

namespace BoostStudio.Core.Tests;

public sealed class DspConfigBuilderTests
{
    private readonly DspConfigBuilder _builder = new();

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void BassAndVolumeAreIndependent(bool bass, bool volume)
    {
        var settings = AppSettings.CreateDefault();
        settings.AutoHeadroom = false;
        settings.Bass.Enabled = bass;
        settings.Bass.Intensity = 60;
        settings.Bass.MaxGainDb = 15;
        settings.Bass.FrequencyHz = 90;
        settings.Volume.Enabled = volume;
        settings.Volume.BoostPercent = 50;
        settings.Volume.CeilingDb = 12;
        settings.Volume.Loudness = 0;
        settings.Equalizer.Enabled = false;

        var config = _builder.Build(settings);

        Assert.True(config.Metrics.BassActive == bass);
        Assert.True(config.Metrics.VolumeActive == volume);
        Assert.False(config.Metrics.EqualizerActive);
        Assert.False(config.Metrics.Bypassed);
        Assert.Empty(DspConfigBuilder.Validate(config.Text));

        if (bass)
        {
            Assert.Equal(1, config.Metrics.FilterCount);
            Assert.Contains("Filter 1: ON LS", config.Text, StringComparison.Ordinal);
            Assert.Contains("Gain 9.0 dB", config.Text, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(0, config.Metrics.FilterCount);
            Assert.DoesNotContain(" ON LS ", config.Text, StringComparison.Ordinal);
        }

        if (volume)
        {
            Assert.Equal(6, config.Metrics.VolumeGainDb, 3);
            Assert.Contains("Volume Booster: digital preamp +6.0 dB", config.Text, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(0, config.Metrics.VolumeGainDb, 3);
            Assert.DoesNotContain("Volume Booster: digital preamp", config.Text, StringComparison.Ordinal);
        }

        if (!bass && !volume)
        {
            Assert.Contains("No processing modules are enabled", config.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("Preamp:", config.Text, StringComparison.Ordinal);
            Assert.Equal(0, config.Metrics.PreampDb, 3);
        }
        else
        {
            Assert.Contains("Preamp:", config.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EqualizerIsIndependentOfBassAndVolume()
    {
        var settings = TestSettings.EqualizerOnly();
        settings.AutoHeadroom = false;
        var config = _builder.Build(settings);

        Assert.True(config.Metrics.EqualizerActive);
        Assert.False(config.Metrics.BassActive);
        Assert.False(config.Metrics.VolumeActive);
        Assert.Equal(3, config.Metrics.FilterCount);
        Assert.Contains("Equalizer: Test", config.Text, StringComparison.Ordinal);
        Assert.Contains("Filter 1: ON PK Fc 62 Hz Gain 2.0 dB", config.Text, StringComparison.Ordinal);
        Assert.Contains("Filter 2: ON PK Fc 250 Hz Gain -3.0 dB", config.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Bass Booster:", config.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Volume Booster:", config.Text, StringComparison.Ordinal);
        Assert.Empty(DspConfigBuilder.Validate(config.Text));
    }

    [Fact]
    public void AutoHeadroomLowersPreampByMeasuredBoost()
    {
        var settings = TestSettings.BassOnly();
        settings.AutoHeadroom = true;
        var config = _builder.Build(settings);

        Assert.True(config.Metrics.HeadroomReservedDb > 0);
        // For bass boost, headroom should be less aggressive to allow audible bass boost.
        // Reserve ~30% of max static boost, not the full amount.
        var expectedHeadroom = Math.Round(Math.Min(config.Metrics.StaticMaxBoostDb * 0.3, config.Metrics.StaticMaxBoostDb + 0.5), 1);
        Assert.Equal(expectedHeadroom, config.Metrics.HeadroomReservedDb, 3);
        Assert.Equal(Math.Round(-config.Metrics.HeadroomReservedDb, 1), config.Metrics.PreampDb, 3);
        Assert.Contains("Auto headroom reserved", config.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void VolumeFoundationReservesHeadroomForLoudnessContour()
    {
        var settings = TestSettings.VolumeOnly();
        settings.AutoHeadroom = true;
        settings.Volume.Loudness = 100;

        var config = _builder.Build(settings);

        Assert.True(config.Metrics.HeadroomReservedDb > 0);
        Assert.Equal(2, config.Metrics.FilterCount);
        Assert.Contains("Loudness contour: 100%", config.Text, StringComparison.Ordinal);
        Assert.Equal(
            Math.Round(config.Metrics.VolumeGainDb - config.Metrics.HeadroomReservedDb, 1),
            config.Metrics.PreampDb, 3);
    }

    [Fact]
    public void RuntimeAgcTrimIsAppliedAndClamped()
    {
        var settings = TestSettings.VolumeOnly();

        var trimmed = _builder.Build(settings, -3.5);
        Assert.Equal(-3.5, trimmed.Metrics.RuntimeAgcTrimDb, 3);
        Assert.Equal(2.5, trimmed.Metrics.PreampDb, 3);
        Assert.Contains("Peak AGC runtime trim: -3.5 dB", trimmed.Text, StringComparison.Ordinal);

        var clampedNegative = _builder.Build(settings, -500);
        Assert.Equal(-24, clampedNegative.Metrics.RuntimeAgcTrimDb, 3);
        Assert.Equal(-18, clampedNegative.Metrics.PreampDb, 3);

        var clampedPositive = _builder.Build(settings, 500);
        Assert.Equal(0, clampedPositive.Metrics.RuntimeAgcTrimDb, 3);
        Assert.Equal(6, clampedPositive.Metrics.PreampDb, 3);
    }

    [Fact]
    public void DeviceScopeIsWrittenOnlyForActiveProcessing()
    {
        var settings = TestSettings.BassOnly();
        var config = _builder.Build(settings, 0, "Speakers (USB DAC)");

        Assert.Contains("Device: Speakers (USB DAC)", config.Text, StringComparison.Ordinal);
        Assert.Contains("Channel: all", config.Text, StringComparison.Ordinal);
        Assert.Empty(DspConfigBuilder.Validate(config.Text));

        settings.ProcessingEnabled = false;
        var bypass = _builder.Build(settings, 0, "Speakers (USB DAC)");
        Assert.DoesNotContain("Device:", bypass.Text, StringComparison.Ordinal);
        Assert.True(bypass.Metrics.Bypassed);
    }

    [Fact]
    public void MasterBypassRetainsAnExplicitBypassConfiguration()
    {
        var settings = TestSettings.BassOnly();
        settings.Volume.Enabled = true;
        settings.Equalizer.Enabled = true;
        settings.ProcessingEnabled = false;

        var config = _builder.Build(settings);

        Assert.True(config.Metrics.Bypassed);
        Assert.False(config.Metrics.BassActive);
        Assert.False(config.Metrics.VolumeActive);
        Assert.False(config.Metrics.EqualizerActive);
        Assert.Equal(0, config.Metrics.FilterCount);
        Assert.Contains("MASTER BYPASS", config.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Filter ", config.Text, StringComparison.Ordinal);
        Assert.Empty(DspConfigBuilder.Validate(config.Text));
    }

    [Fact]
    public void GeneratedConfigurationUsesOnlySupportedGrammar()
    {
        var settings = AppSettings.CreateDefault();
        settings.Bass.Enabled = true;
        settings.Volume.Enabled = true;
        settings.Volume.Loudness = 80;
        settings.Equalizer.Enabled = true;
        settings.Equalizer.GainsDb = new double[] { 4, 3, 2, 1, 0, -1, -2, -3, -4, -5 };
        settings.AutoHeadroom = true;

        var config = _builder.Build(settings, -1.5, "Device A");
        var errors = DspConfigBuilder.Validate(config.Text);

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("Preamp: nope dB", "invalid Preamp")]
    [InlineData("Filter 1: NOPE Fc 100 Hz Gain 3 dB Q 1.0", "missing ON")]
    [InlineData("Filter 1: ON PK Gain 3 dB Q 1.0", "missing Fc")]
    [InlineData("GraphicEQ: 20 0 100 3", "unsupported command")]
    public void ValidateReportsUnsupportedOrMalformedLines(string line, string expected)
    {
        var errors = DspConfigBuilder.Validate("# comment\n" + line);

        Assert.Contains(errors, error => error.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidateAcceptsCommentsAndBlankLines()
    {
        Assert.Empty(DspConfigBuilder.Validate("""
            # header
            Preamp: -6.0 dB

            Channel: all
            Filter 1: ON LS Fc 80.0 Hz Gain 6.0 dB Q 0.707
            Device: all
            """));
    }

    [Fact]
    public void HeadroomCanBeDisabledAndIsReportedInConfiguration()
    {
        var settings = TestSettings.BassOnly();
        settings.AutoHeadroom = false;
        var config = _builder.Build(settings);

        Assert.Equal(0, config.Metrics.HeadroomReservedDb, 3);
        Assert.Equal(0, config.Metrics.PreampDb, 3);
        Assert.Contains("Auto headroom is disabled", config.Text, StringComparison.Ordinal);
    }
}
