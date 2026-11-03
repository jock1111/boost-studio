using BoostStudio.Core.Dsp;
using BoostStudio.Core.Models;

namespace BoostStudio.Core.Tests;

public sealed class DeviceScopingTests
{
    private readonly DspConfigBuilder _builder = new();

    [Fact]
    public void ApplyToAllDevicesWritesNoDeviceLine()
    {
        var settings = TestSettings.BassOnly();
        settings.Audio.ApplyToAllDevices = true;
        settings.Audio.ScopedDevicePattern = null;

        var config = _builder.Build(settings, 0);

        Assert.DoesNotContain("Device:", config.Text, StringComparison.Ordinal);
        Assert.Contains("Channel: all", config.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ScopedDeviceWritesDeviceLine()
    {
        var settings = TestSettings.BassOnly();
        settings.Audio.ApplyToAllDevices = false;
        settings.Audio.ScopedDevicePattern = "Speakers (Realtek)";

        var config = _builder.Build(settings, 0, settings.Audio.ScopedDevicePattern);

        Assert.Contains("Device: Speakers (Realtek)", config.Text, StringComparison.Ordinal);
        Assert.Contains("Channel: all", config.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyPatternFallsBackToApplyAll()
    {
        var settings = TestSettings.BassOnly();
        settings.Audio.ApplyToAllDevices = false;
        settings.Audio.ScopedDevicePattern = "  ";
        settings.Normalize();

        Assert.True(settings.Audio.ApplyToAllDevices); // Normalize converts empty pattern to ApplyToAllDevices=true
        Assert.Null(settings.Audio.ScopedDevicePattern);

        var config = _builder.Build(settings, 0);
        Assert.DoesNotContain("Device:", config.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void MasterBypassHonorsDeviceScope()
    {
        var settings = TestSettings.BassOnly();
        settings.Audio.ApplyToAllDevices = false;
        settings.Audio.ScopedDevicePattern = "Headphones";
        settings.ProcessingEnabled = false;

        var config = _builder.Build(settings, 0);

        Assert.True(config.Metrics.Bypassed);
        Assert.DoesNotContain("Device:", config.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void MonitoringDeviceIdIsSeparateFromDeviceScope()
    {
        var settings = TestSettings.BassOnly();
        settings.Audio.MonitoringDeviceId = "device-1";
        settings.Audio.MonitorDefaultDevice = false;
        settings.Audio.ApplyToAllDevices = true;
        settings.Audio.ScopedDeviceId = "device-2"; // Cleared by Normalize when ApplyToAllDevices is true

        settings.Normalize();

        Assert.Equal("device-1", settings.Audio.MonitoringDeviceId);
        Assert.False(settings.Audio.MonitorDefaultDevice);
        Assert.Null(settings.Audio.ScopedDeviceId); // Cleared by Normalize when ApplyToAllDevices is true
    }
}
