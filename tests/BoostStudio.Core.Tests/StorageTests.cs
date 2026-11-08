using System.IO.Compression;
using System.Text;
using BoostStudio.Core.Models;
using BoostStudio.Core.Storage;

namespace BoostStudio.Core.Tests;

public sealed class StorageTests
{
    [Fact]
    public void SettingsRoundTripPreservesAllModules()
    {
        using var root = new TempDirectory();
        var store = new SettingsStore(root.File("settings.json"));
        var settings = AppSettings.CreateDefault();
        settings.ProcessingEnabled = true;
        settings.Bass.Enabled = true;
        settings.Bass.Intensity = 73;
        settings.Volume.Enabled = true;
        settings.Volume.BoostPercent = 42;
        settings.Volume.Loudness = 0;
        settings.Equalizer.Enabled = true;
        settings.Equalizer.GainsDb = new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        settings.Equalizer.SelectedPresetName = "Mine";
        settings.Application.HasSeenWelcome = true;
        settings.Hotkeys.ToggleBass = "Ctrl+Shift+B";

        store.Save(settings);
        var loaded = store.Load();

        Assert.Equal(settings.ProcessingEnabled, loaded.ProcessingEnabled);
        Assert.Equal(73, loaded.Bass.Intensity);
        Assert.Equal(42, loaded.Volume.BoostPercent);
        Assert.Equal(0, loaded.Volume.Loudness);
        Assert.True(loaded.Equalizer.Enabled);
        Assert.Equal(settings.Equalizer.GainsDb, loaded.Equalizer.GainsDb);
        Assert.Equal("Mine", loaded.Equalizer.SelectedPresetName);
        Assert.True(loaded.Application.HasSeenWelcome);
        Assert.Equal("Ctrl+Shift+B", loaded.Hotkeys.ToggleBass);
    }

    [Fact]
    public void SettingsAreNormalizedOnSaveAndLoad()
    {
        using var root = new TempDirectory();
        var store = new SettingsStore(root.File("settings.json"));
        var settings = AppSettings.CreateDefault();
        settings.Bass.Intensity = 999;
        settings.Bass.FrequencyHz = -20;
        settings.Bass.MaxGainDb = 100;
        settings.Bass.ShelfQ = 5;
        settings.Volume.BoostPercent = -5;
        settings.Volume.CeilingDb = 100;
        settings.Volume.PreampDb = -100;
        settings.Volume.Loudness = 500;
        settings.Equalizer.GainsDb = new double[] { 20, -20 };

        store.Save(settings);
        var loaded = store.Load();

        Assert.Equal(100, loaded.Bass.Intensity);
        Assert.Equal(20, loaded.Bass.FrequencyHz);
        Assert.Equal(24, loaded.Bass.MaxGainDb);
        Assert.Equal(1.4, loaded.Bass.ShelfQ);
        Assert.Equal(0, loaded.Volume.BoostPercent);
        Assert.Equal(24, loaded.Volume.CeilingDb);
        Assert.Equal(-12, loaded.Volume.PreampDb);
        Assert.Equal(100, loaded.Volume.Loudness);
        Assert.Equal(10, loaded.Equalizer.GainsDb.Length);
        Assert.Equal(12, loaded.Equalizer.GainsDb[0]);
        Assert.Equal(-12, loaded.Equalizer.GainsDb[1]);
        Assert.Equal(0, loaded.Equalizer.GainsDb[9]);
    }

    [Fact]
    public void CorruptSettingsRecoverFromAutomaticBackup()
    {
        using var root = new TempDirectory();
        var path = root.File("settings.json");
        var store = new SettingsStore(path);

        var first = AppSettings.CreateDefault();
        first.Bass.Intensity = 11;
        store.Save(first);

        var second = AppSettings.CreateDefault();
        second.Bass.Intensity = 22;
        store.Save(second);
        Assert.True(File.Exists(path + ".bak"));

        File.WriteAllText(path, "{ this is not json");

        var recovered = store.Load();

        Assert.Equal(11, recovered.Bass.Intensity);
        Assert.False(File.Exists(path));
        Assert.NotEmpty(Directory.GetFiles(root.Path, "settings.json.corrupt-*"));
    }

    [Fact]
    public void InvalidSettingsAndBackupReturnDefaults()
    {
        using var root = new TempDirectory();
        var path = root.File("settings.json");
        var store = new SettingsStore(path);
        File.WriteAllText(path, "{ bad");
        File.WriteAllText(path + ".bak", "also bad");

        var loaded = store.Load();

        Assert.Equal(AppInfo.SettingsSchemaVersion, loaded.SchemaVersion);
        Assert.Equal(45, loaded.Bass.Intensity);
    }

    [Fact]
    public void OldImplicitLoudnessDefaultMigratesToNeutral()
    {
        using var root = new TempDirectory();
        var path = root.File("settings.json");
        var store = new SettingsStore(path);
        File.WriteAllText(path, "{\"SchemaVersion\":1,\"Volume\":{\"Enabled\":true,\"Loudness\":20}}");

        var loaded = store.Load();

        Assert.Equal(0, loaded.Volume.Loudness);
        Assert.Equal(AppInfo.SettingsSchemaVersion, loaded.SchemaVersion);
        Assert.Equal(2, AppInfo.SettingsSchemaVersion);
    }

    [Fact]
    public void ExplicitLoudnessInCurrentSchemaIsPreserved()
    {
        using var root = new TempDirectory();
        var path = root.File("settings.json");
        var store = new SettingsStore(path);
        File.WriteAllText(path, "{\"SchemaVersion\":2,\"Volume\":{\"Loudness\":20}}");

        var loaded = store.Load();

        Assert.Equal(20, loaded.Volume.Loudness);
    }

    [Fact]
    public void PresetsRoundTripAndNormalizeDuplicates()
    {
        using var root = new TempDirectory();
        var store = new PresetStore(root.File("presets.json"));
        var library = new UserPresetLibrary();
        library.EqualizerPresets.Add(new UserEqPreset
        {
            Name = "  My Curve  ",
            GainsDb = new double[] { 20, -20, 3 }
        });
        library.EqualizerPresets.Add(new UserEqPreset
        {
            Name = "my curve",
            GainsDb = new double[] { 9, 9, 9 }
        });
        library.EqualizerPresets.Add(new UserEqPreset
        {
            Name = "Second",
            GainsDb = new double[] { 2, 4, 6, 8, 10, 12, 14, 16, 18, 20 }
        });

        store.Save(library);
        var loaded = store.Load();

        Assert.Equal(2, loaded.EqualizerPresets.Count);
        var first = loaded.EqualizerPresets[0];
        Assert.Equal("My Curve", first.Name);
        Assert.Equal(10, first.GainsDb.Length);
        Assert.Equal(12, first.GainsDb[0]);
        Assert.Equal(-12, first.GainsDb[1]);
        Assert.Equal(3, first.GainsDb[2]);
        Assert.Equal(12, loaded.EqualizerPresets[1].GainsDb[9]);
    }

    [Fact]
    public void CorruptPresetFileLoadsEmptyLibrary()
    {
        using var root = new TempDirectory();
        var path = root.File("presets.json");
        File.WriteAllText(path, "not json");
        var store = new PresetStore(path);

        var library = store.Load();

        Assert.Empty(library.EqualizerPresets);
    }

    [Fact]
    public void BackupExportImportRoundTrip()
    {
        using var root = new TempDirectory();
        var settingsStore = new SettingsStore(root.File("settings.json"));
        var presetStore = new PresetStore(root.File("presets.json"));
        var service = new BackupService(settingsStore, presetStore);

        var settings = AppSettings.CreateDefault();
        settings.Bass.Enabled = true;
        settings.Bass.Intensity = 64;
        settings.Volume.Enabled = true;
        settings.Hotkeys.ToggleBass = "Ctrl+Shift+J";
        var presets = new UserPresetLibrary();
        presets.EqualizerPresets.Add(new UserEqPreset
        {
            Name = "Portable",
            GainsDb = Enumerable.Range(0, 10).Select(i => (double)i / 2).ToArray()
        });

        var path = root.File("state.boostbackup");
        service.Export(path, settings, presets, "diagnostic sample");

        Assert.True(File.Exists(path));
        using (var archive = ZipFile.OpenRead(path))
        {
            Assert.NotNull(archive.GetEntry("settings.json"));
            Assert.NotNull(archive.GetEntry("presets.json"));
            Assert.NotNull(archive.GetEntry("manifest.json"));
            Assert.NotNull(archive.GetEntry("diagnostics.txt"));
        }

        var result = service.Import(path);

        Assert.True(result.Success);
        Assert.NotNull(result.Settings);
        Assert.NotNull(result.Presets);
        Assert.Equal(64, result.Settings!.Bass.Intensity);
        Assert.Equal("Ctrl+Shift+J", result.Settings.Hotkeys.ToggleBass);
        Assert.Single(result.Presets!.EqualizerPresets);
        Assert.Equal("Portable", result.Presets.EqualizerPresets[0].Name);
    }

    [Fact]
    public void BackupRoundTripIncludesSoundProfiles()
    {
        using var root = new TempDirectory();
        var settingsStore = new SettingsStore(root.File("settings.json"));
        var presetStore = new PresetStore(root.File("presets.json"));
        var profileStore = new ProfileStore(root.File("profiles.json"));
        var service = new BackupService(settingsStore, presetStore, profileStore: profileStore);

        var settings = AppSettings.CreateDefault();
        settings.Bass.Enabled = true;
        settings.Bass.Intensity = 71;
        var profiles = new ProfileLibrary();
        var profile = SoundProfile.Capture("Night listening", settings);
        profiles.Profiles.Add(profile);
        profiles.ActiveProfileId = profile.Id;

        var path = root.File("profiles.boostbackup");
        service.Export(path, settings, new UserPresetLibrary(), profiles: profiles);

        using (var archive = ZipFile.OpenRead(path))
        {
            Assert.NotNull(archive.GetEntry("profiles.json"));
            var manifest = archive.GetEntry("manifest.json")!;
            using var reader = new StreamReader(manifest.Open(), Encoding.UTF8);
            Assert.Contains("\"format\": 2", reader.ReadToEnd());
        }

        var result = service.Import(path);

        Assert.True(result.Success);
        Assert.NotNull(result.Profiles);
        Assert.Single(result.Profiles!.Profiles);
        Assert.Equal("Night listening", result.Profiles.Profiles[0].Name);
        Assert.Equal(profile.Id, result.Profiles.ActiveProfileId);

        service.ApplyImported(result.Settings!, result.Presets!, result.Profiles);

        var reloaded = profileStore.Load();
        Assert.Single(reloaded.Profiles);
        Assert.Equal(71, reloaded.Profiles[0].Bass.Intensity);
        Assert.Equal(profile.Id, reloaded.ActiveProfileId);
    }

    [Fact]
    public void BackupWithoutProfilesReportsNoProfileSection()
    {
        using var root = new TempDirectory();
        var service = new BackupService(
            new SettingsStore(root.File("settings.json")),
            new PresetStore(root.File("presets.json")));
        var path = root.File("legacy.boostbackup");
        service.Export(path, AppSettings.CreateDefault(), new UserPresetLibrary());

        using (var archive = ZipFile.OpenRead(path))
            Assert.Null(archive.GetEntry("profiles.json"));

        var result = service.Import(path);

        Assert.True(result.Success);
        Assert.Null(result.Profiles);
    }

    [Fact]
    public void BackupImportRejectsFilesWithoutSettingsEntry()
    {
        using var root = new TempDirectory();
        var service = new BackupService(
            new SettingsStore(root.File("settings.json")),
            new PresetStore(root.File("presets.json")));
        var path = root.File("invalid.boostbackup");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("notes.txt");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write("nothing useful");
        }

        var result = service.Import(path);

        Assert.False(result.Success);
        Assert.Contains("settings.json", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BackupImportRejectsNonZipFiles()
    {
        using var root = new TempDirectory();
        var service = new BackupService(
            new SettingsStore(root.File("settings.json")),
            new PresetStore(root.File("presets.json")));
        var path = root.File("bad.boostbackup");
        File.WriteAllText(path, "not a zip archive");

        var result = service.Import(path);

        Assert.False(result.Success);
        Assert.Null(result.Settings);
    }

    [Fact]
    public void BackupImportIgnoresUnexpectedEntriesAndAppliesImportedState()
    {
        using var root = new TempDirectory();
        var settingsStore = new SettingsStore(root.File("settings.json"));
        var presetStore = new PresetStore(root.File("presets.json"));
        var service = new BackupService(settingsStore, presetStore);
        var path = root.File("state.boostbackup");
        var settings = AppSettings.CreateDefault();
        settings.Equalizer.Enabled = true;
        service.Export(path, settings, new UserPresetLibrary());

        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = archive.CreateEntry("../escape.txt");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write("must never be extracted");
        }

        var result = service.Import(path);
        Assert.True(result.Success);
        Assert.False(File.Exists(Path.Combine(root.Path, "..", "escape.txt")));

        service.ApplyImported(result.Settings!, result.Presets!);
        Assert.True(settingsStore.Load().Equalizer.Enabled);
        Assert.Empty(presetStore.Load().EqualizerPresets);
    }
}
