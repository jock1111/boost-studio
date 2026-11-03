using BoostStudio.Core.Models;

namespace BoostStudio.Core.Tests;

public sealed class SettingsAndPresetsTests
{
    [Fact]
    public void DefaultsAreSafeAndExpected()
    {
        var settings = AppSettings.CreateDefault();

        Assert.True(settings.ProcessingEnabled);
        Assert.True(settings.AutoHeadroom);
        Assert.False(settings.Bass.Enabled);
        Assert.False(settings.Volume.Enabled);
        Assert.False(settings.Equalizer.Enabled);
        Assert.Equal(45, settings.Bass.Intensity);
        Assert.Equal(80, settings.Bass.FrequencyHz);
        Assert.Equal(10, settings.Equalizer.GainsDb.Length);
        Assert.Equal("dashboard", settings.LastPage);
        Assert.True(settings.Application.MinimizeToTray);
        Assert.True(settings.Application.CloseToTray);
        Assert.Equal("Ctrl+Alt+B", settings.Hotkeys.ToggleBass);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void BassAndVolumeIndependenceIsRepresentedInSettings(bool bass, bool volume, bool expectedAny)
    {
        var settings = AppSettings.CreateDefault();
        settings.Bass.Enabled = bass;
        settings.Volume.Enabled = volume;
        settings.Equalizer.Enabled = false;

        Assert.Equal(expectedAny, settings.AnyProcessingEnabled);
        Assert.Equal(bass, settings.Bass.Enabled);
        Assert.Equal(volume, settings.Volume.Enabled);
    }

    [Fact]
    public void MasterSwitchOverridesModuleState()
    {
        var settings = AppSettings.CreateDefault();
        settings.Bass.Enabled = true;

        settings.ProcessingEnabled = false;
        Assert.False(settings.AnyProcessingEnabled);

        settings.ProcessingEnabled = true;
        Assert.True(settings.AnyProcessingEnabled);
    }

    [Fact]
    public void CopyFromDoesNotShareMutableCollections()
    {
        var source = AppSettings.CreateDefault();
        source.Equalizer.GainsDb[2] = 7;
        source.Bass.Enabled = true;
        source.Volume.PreampDb = 3;
        var copy = AppSettings.CreateDefault();

        copy.CopyFrom(source);

        Assert.True(copy.Bass.Enabled);
        Assert.Equal(3, copy.Volume.PreampDb);
        copy.Equalizer.GainsDb[2] = -5;
        Assert.Equal(7, source.Equalizer.GainsDb[2]);
        Assert.NotSame(source.Equalizer.GainsDb, copy.Equalizer.GainsDb);
    }

    [Fact]
    public void AudioScopeNormalizesRestrictiveCombinations()
    {
        var settings = AppSettings.CreateDefault();
        settings.Audio.MonitorDefaultDevice = true;
        settings.Audio.MonitoringDeviceId = "device-1";
        settings.Audio.ApplyToAllDevices = true;
        settings.Audio.ScopedDeviceId = "scope";
        settings.Audio.ScopedDevicePattern = "Speakers";
        settings.Normalize();

        Assert.Null(settings.Audio.MonitoringDeviceId);
        Assert.Null(settings.Audio.ScopedDeviceId);
        Assert.Null(settings.Audio.ScopedDevicePattern);

        settings.Audio.ApplyToAllDevices = false;
        settings.Audio.ScopedDeviceId = "scope";
        settings.Audio.ScopedDevicePattern = null;
        settings.Normalize();

        Assert.True(settings.Audio.ApplyToAllDevices);
        Assert.Null(settings.Audio.ScopedDeviceId);
    }

    [Fact]
    public void InvalidLastPageFallsBackToDashboard()
    {
        var settings = AppSettings.CreateDefault();
        settings.LastPage = "does-not-exist";
        settings.Normalize();

        Assert.Equal("dashboard", settings.LastPage);
    }

    [Fact]
    public void EffectiveGainUsesPercentOfCeiling()
    {
        var bass = new BassSettings { MaxGainDb = 20, Intensity = 50 };
        var volume = new VolumeSettings { CeilingDb = 12, BoostPercent = 25 };

        Assert.Equal(10, bass.EffectiveGainDb, 3);
        Assert.Equal(3, volume.EffectiveBoostDb, 3);

        bass.Intensity = 0;
        volume.BoostPercent = 0;
        Assert.Equal(0, bass.EffectiveGainDb, 3);
        Assert.Equal(0, volume.EffectiveBoostDb, 3);
    }

    [Fact]
    public void BassPresetsApplyAndDetect()
    {
        var settings = new BassSettings();
        var punchy = BassPresets.Find("punchy bass");
        Assert.NotNull(punchy);

        BassPresets.Apply(settings, punchy!);

        Assert.Equal("Punchy Bass", settings.PresetName);
        Assert.Equal(punchy.Intensity, settings.Intensity);
        Assert.Equal("Punchy Bass", BassPresets.DetectPreset(settings));

        settings.Intensity = 1;
        Assert.Equal(BassPresets.Custom, BassPresets.DetectPreset(settings));
        Assert.Null(BassPresets.Find("missing"));
        Assert.False(BassPresets.Matches(settings, BassPresets.All[4]));
    }

    [Fact]
    public void EqualizerPresetsDetectBuiltInsAndCustomCurves()
    {
        var music = EqPresets.Find("Music");
        Assert.NotNull(music);
        Assert.Equal("Music", EqPresets.Detect((double[])music!.GainsDb.Clone()));

        var custom = new double[EqConstants.BandCount];
        custom[0] = 3;
        Assert.Equal(EqPresets.Custom, EqPresets.Detect(custom));
        Assert.False(EqPresets.Matches(custom, music));
        Assert.Null(EqPresets.Find("no such preset"));
    }

    [Fact]
    public void UserPresetNamesAreTrimmedTruncatedAndDeduplicated()
    {
        var library = new UserPresetLibrary();
        library.EqualizerPresets.Add(new UserEqPreset
        {
            Name = "  " + new string('a', 60) + "  ",
            GainsDb = new double[EqConstants.BandCount]
        });
        library.EqualizerPresets.Add(new UserEqPreset
        {
            Name = new string('a', 40),
            GainsDb = new double[EqConstants.BandCount]
        });
        library.EqualizerPresets.Add(new UserEqPreset
        {
            Name = "   ",
            GainsDb = new double[EqConstants.BandCount]
        });

        library.Normalize();

        Assert.Single(library.EqualizerPresets);
        Assert.Equal(40, library.EqualizerPresets[0].Name.Length);
    }

    [Fact]
    public void HotkeyDefaultsHonorBlankValues()
    {
        var hotkeys = new HotkeySettings
        {
            ToggleBass = " ",
            ToggleVolume = "",
            ToggleProcessing = ""
        };

        hotkeys.Normalize();

        Assert.Equal("Ctrl+Alt+B", hotkeys.ToggleBass);
        Assert.Equal("Ctrl+Alt+V", hotkeys.ToggleVolume);
        Assert.Equal("Ctrl+Alt+M", hotkeys.ToggleProcessing);
    }
}
