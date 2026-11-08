using BoostStudio.Core.Engine;
using BoostStudio.Core.Models;

namespace BoostStudio.Core.Tests;

public sealed class ApoReadinessTests
{
    [Fact]
    public void AllDevicesUsesTheWindowsDefaultOutputAsTheTarget()
    {
        var probe = new AudioDeviceApoReadinessProbe(deviceSource: () => new[]
        {
            Device("default", "Speakers", isDefault: true, apo: false),
            Device("headphones", "Headphones", isDefault: false, apo: true)
        });

        var result = probe.Evaluate(new AudioSettings { ApplyToAllDevices = true });

        Assert.True(result.Checked);
        Assert.Equal(2, result.DeviceCount);
        Assert.Equal(1, result.ApoEnabledDeviceCount);
        Assert.Equal("Speakers", result.TargetDeviceName);
        Assert.Equal("Speakers", result.Scope);
        Assert.False(result.TargetApoEnabled);
        Assert.True(result.DefaultDeviceApoEnabled == false);
    }

    [Fact]
    public void AllDevicesIsReadyWhenTheDefaultOutputHasTheApo()
    {
        var probe = new AudioDeviceApoReadinessProbe(deviceSource: () => new[]
        {
            Device("default", "Speakers", isDefault: true, apo: true),
            Device("headphones", "Headphones", isDefault: false, apo: false)
        });

        var result = probe.Evaluate(new AudioSettings { ApplyToAllDevices = true });

        Assert.True(result.TargetApoEnabled);
        Assert.Equal("Speakers", result.TargetDeviceName);
        Assert.Equal(1, result.ApoEnabledDeviceCount);
    }

    [Fact]
    public void ScopedModeTargetsTheSelectedEndpointInsteadOfTheDefault()
    {
        var probe = new AudioDeviceApoReadinessProbe(deviceSource: () => new[]
        {
            Device("default", "Speakers", isDefault: true, apo: false),
            Device("headphones", "Headphones", isDefault: false, apo: true)
        });

        var result = probe.Evaluate(new AudioSettings
        {
            ApplyToAllDevices = false,
            ScopedDeviceId = "headphones",
            ScopedDevicePattern = "Headphones"
        });

        Assert.True(result.TargetApoEnabled);
        Assert.Equal("Headphones", result.TargetDeviceName);
        Assert.False(result.DefaultDeviceApoEnabled);
        Assert.Equal("Speakers", result.DefaultDeviceName);
    }

    [Fact]
    public void AllDevicesSuggestsAnEnabledOutputWhenTheDefaultIsUnprocessed()
    {
        var probe = new AudioDeviceApoReadinessProbe(deviceSource: () => new[]
        {
            Device("default", "Speakers", isDefault: true, apo: false),
            Device("headphones", "Headphones", isDefault: false, apo: true)
        });

        var result = probe.Evaluate(new AudioSettings { ApplyToAllDevices = true });

        Assert.False(result.TargetApoEnabled);
        Assert.Equal("Headphones", result.SuggestedApoDeviceName);
        Assert.Equal("headphones", result.SuggestedApoDeviceId);
    }

    [Fact]
    public void AllDevicesDoesNotSuggestAnotherOutputWhenTheDefaultIsEnabled()
    {
        var probe = new AudioDeviceApoReadinessProbe(deviceSource: () => new[]
        {
            Device("default", "Speakers", isDefault: true, apo: true),
            Device("headphones", "Headphones", isDefault: false, apo: true)
        });

        var result = probe.Evaluate(new AudioSettings { ApplyToAllDevices = true });

        Assert.True(result.TargetApoEnabled);
        Assert.Null(result.SuggestedApoDeviceName);
        Assert.Null(result.SuggestedApoDeviceId);
    }

    [Fact]
    public void ScopedModeSuggestsAnEnabledEndpointWhenTheSelectedOneIsUnprocessed()
    {
        var probe = new AudioDeviceApoReadinessProbe(deviceSource: () => new[]
        {
            Device("default", "Speakers", isDefault: true, apo: false),
            Device("headphones", "Headphones", isDefault: false, apo: true)
        });

        var result = probe.Evaluate(new AudioSettings
        {
            ApplyToAllDevices = false,
            ScopedDeviceId = "default",
            ScopedDevicePattern = "Speakers"
        });

        Assert.False(result.TargetApoEnabled);
        Assert.Equal("Headphones", result.SuggestedApoDeviceName);
        Assert.Equal("headphones", result.SuggestedApoDeviceId);
    }

    [Fact]
    public void BluetoothOutputsCanBeSuggestedWhenTheyHaveTheApo()
    {
        // Bluetooth endpoints are valid APO targets on Windows 11; the measured
        // self-test confirmed processing reaches a Bluetooth speaker's loopback.
        var probe = new AudioDeviceApoReadinessProbe(deviceSource: () => new[]
        {
            Device("default", "BT Speaker", isDefault: true, apo: false),
            BluetoothDevice("bt", "Kevin speaker", apo: true),
            Device("wired", "Speakers", isDefault: false, apo: true)
        });

        var result = probe.Evaluate(new AudioSettings { ApplyToAllDevices = true });

        Assert.False(result.TargetApoEnabled);
        Assert.Equal("Kevin speaker", result.SuggestedApoDeviceName);
        Assert.Equal("bt", result.SuggestedApoDeviceId);
    }

    [Fact]
    public void BluetoothOnlyApoOutputIsSuggested()
    {
        var probe = new AudioDeviceApoReadinessProbe(deviceSource: () => new[]
        {
            Device("default", "BT Speaker", isDefault: true, apo: false),
            BluetoothDevice("bt", "Kevin speaker", apo: true)
        });

        var result = probe.Evaluate(new AudioSettings { ApplyToAllDevices = true });

        Assert.False(result.TargetApoEnabled);
        Assert.Equal(1, result.ApoEnabledDeviceCount);
        Assert.Equal("Kevin speaker", result.SuggestedApoDeviceName);
        Assert.Equal("bt", result.SuggestedApoDeviceId);
    }

    [Fact]
    public void MissingDevicesAreReportedAsUnavailable()
    {
        var probe = new AudioDeviceApoReadinessProbe(deviceSource: () => Array.Empty<AudioDeviceInfo>());

        var result = probe.Evaluate(new AudioSettings { ApplyToAllDevices = true });

        Assert.False(result.Checked);
        Assert.Equal(0, result.DeviceCount);
        Assert.False(result.TargetApoEnabled);
    }

    private static AudioDeviceInfo Device(string id, string name, bool isDefault, bool apo) =>
        new(id, name, name, null, isDefault, 48000, 2, "Active", name, apo);

    private static AudioDeviceInfo BluetoothDevice(string id, string name, bool apo) =>
        new(id, name, name, null, false, 48000, 2, "Active", name, apo, true);
}
