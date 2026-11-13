using BoostStudio.Core.Models;
using BoostStudio.Core.Storage;

namespace BoostStudio.Core.Tests;

public sealed class SoundProfileTests
{
    [Fact]
    public void CaptureAndApplyRoundTripsProcessingStateOnly()
    {
        var settings = AppSettings.CreateDefault();
        settings.ProcessingEnabled = true;
        settings.AutoHeadroom = false;
        settings.Bass.Enabled = true;
        settings.Bass.Intensity = 82;
        settings.Bass.FrequencyHz = 63;
        settings.Bass.MaxGainDb = 21;
        settings.Bass.ShelfQ = 1.1;
        settings.Bass.ProtectHeadroom = true;
        settings.Bass.PresetName = "Deep Bass";
        settings.Volume.Enabled = true;
        settings.Volume.BoostPercent = 55;
        settings.Volume.CeilingDb = 18;
        settings.Volume.PreampDb = -2.5;
        settings.Volume.Loudness = 35;
        settings.Volume.AutoGainControl = true;
        settings.Equalizer.Enabled = true;
        settings.Equalizer.GainsDb = new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        settings.Equalizer.SelectedPresetName = "Music";
        settings.Application.StartWithWindows = true;
        settings.Audio.ScopedDevicePattern = "Speakers";

        var profile = SoundProfile.Capture("  Loud  ", settings);
        Assert.Equal("Loud", profile.Name);
        Assert.Equal(82, profile.Bass.Intensity);
        Assert.Equal("Music", profile.Equalizer.SelectedPresetName);

        settings.ProcessingEnabled = false;
        settings.AutoHeadroom = true;
        settings.Bass.Enabled = false;
        settings.Bass.Intensity = 1;
        settings.Bass.ProtectHeadroom = false;
        settings.Volume.Enabled = false;
        settings.Volume.BoostPercent = 3;
        settings.Equalizer.Enabled = false;
        settings.Equalizer.GainsDb = new double[10];
        settings.Equalizer.SelectedPresetName = "Custom";
        settings.Application.StartWithWindows = false;
        settings.Audio.ScopedDevicePattern = null;

        profile.ApplyTo(settings);

        Assert.True(settings.ProcessingEnabled);
        Assert.False(settings.AutoHeadroom);
        Assert.True(settings.Bass.Enabled);
        Assert.Equal(82, settings.Bass.Intensity);
        Assert.Equal(63, settings.Bass.FrequencyHz);
        Assert.Equal(21, settings.Bass.MaxGainDb);
        Assert.Equal(1.1, settings.Bass.ShelfQ);
        Assert.True(settings.Bass.ProtectHeadroom);
        Assert.Equal("Deep Bass", settings.Bass.PresetName);
        Assert.True(settings.Volume.Enabled);
        Assert.Equal(55, settings.Volume.BoostPercent);
        Assert.Equal(18, settings.Volume.CeilingDb);
        Assert.Equal(-2.5, settings.Volume.PreampDb);
        Assert.Equal(35, settings.Volume.Loudness);
        Assert.True(settings.Volume.AutoGainControl);
        Assert.True(settings.Equalizer.Enabled);
        Assert.Equal(new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, settings.Equalizer.GainsDb);

        // Application preferences, hotkeys and routing deliberately stay live.
        Assert.False(settings.Application.StartWithWindows);
        Assert.Null(settings.Audio.ScopedDevicePattern);
        Assert.True(profile.Matches(settings));
    }

    [Fact]
    public void MatchesDetectsProcessingChangesButIgnoresMetadata()
    {
        var settings = AppSettings.CreateDefault();
        settings.Bass.Enabled = true;
        settings.Bass.Intensity = 60;
        settings.Volume.Enabled = true;
        settings.Volume.BoostPercent = 40;
        var profile = SoundProfile.Capture("One", settings);

        settings.Bass.PresetName = "Custom";
        settings.Equalizer.SelectedPresetName = "Something else";
        profile.ModifiedUtc = DateTime.UtcNow.AddDays(2);
        Assert.True(profile.Matches(settings));

        settings.Bass.ProtectHeadroom = !settings.Bass.ProtectHeadroom;
        Assert.False(profile.Matches(settings));
        settings.Bass.ProtectHeadroom = !settings.Bass.ProtectHeadroom;

        settings.Volume.BoostPercent = 41;
        Assert.False(profile.Matches(settings));
    }

    [Fact]
    public void CapturedEqualizerCurveIsIndependentOfLiveSettings()
    {
        var settings = AppSettings.CreateDefault();
        settings.Equalizer.GainsDb = new double[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 };
        var profile = SoundProfile.Capture("Snapshot", settings);

        settings.Equalizer.GainsDb[0] = 9;
        Assert.Equal(1, profile.Equalizer.GainsDb[0]);

        profile.ApplyTo(settings);
        profile.Equalizer.GainsDb[1] = 7;
        settings.Equalizer.GainsDb[1] = -4;
        Assert.Equal(7, profile.Equalizer.GainsDb[1]);
        Assert.Equal(-4, settings.Equalizer.GainsDb[1]);
    }

    [Fact]
    public void LibraryNormalizesDuplicateIdsAndNames()
    {
        var library = new ProfileLibrary();
        library.Profiles.Add(new SoundProfile { Id = "duplicate", Name = "Warm" });
        library.Profiles.Add(new SoundProfile { Id = "duplicate", Name = "warm" });
        library.Profiles.Add(new SoundProfile { Id = "not-a-guid", Name = "  " });
        library.ActiveProfileId = "missing";

        library.Normalize();

        Assert.Equal(3, library.Profiles.Count);
        Assert.Equal(3, library.Profiles.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(3, library.Profiles.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(library.Profiles, p => string.Equals(p.Name, "Warm (2)", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(library.Profiles, p => p.Name == "Untitled profile");
        Assert.All(library.Profiles, p => Assert.Matches("^[0-9a-f]{32}$", p.Id));
        Assert.Null(library.ActiveProfileId);
    }

    [Fact]
    public void ProfileStoreRoundTripsAndRecoversFromBackup()
    {
        using var root = new TempDirectory();
        var path = root.File("profiles.json");
        var store = new ProfileStore(path);

        var first = new ProfileLibrary();
        var saved = SoundProfile.Capture("First", AppSettings.CreateDefault());
        first.Profiles.Add(saved);
        first.ActiveProfileId = saved.Id;
        store.Save(first);

        var second = new ProfileLibrary();
        second.Profiles.Add(SoundProfile.Capture("Second", AppSettings.CreateDefault()));
        store.Save(second);
        Assert.True(File.Exists(path + ".bak"));

        var loaded = store.Load();
        Assert.Single(loaded.Profiles);
        Assert.Equal("Second", loaded.Profiles[0].Name);

        File.WriteAllText(path, "{ not valid json");
        var recovered = store.Load();

        Assert.Single(recovered.Profiles);
        Assert.Equal("First", recovered.Profiles[0].Name);
        Assert.Equal(saved.Id, recovered.ActiveProfileId);
        Assert.False(File.Exists(path));
        Assert.NotEmpty(Directory.GetFiles(root.Path, "profiles.json.corrupt-*"));
    }

    [Fact]
    public void MissingProfileFileLoadsEmptyLibrary()
    {
        using var root = new TempDirectory();
        var library = new ProfileStore(root.File("profiles.json")).Load();

        Assert.Empty(library.Profiles);
        Assert.Null(library.ActiveProfileId);
    }
}
