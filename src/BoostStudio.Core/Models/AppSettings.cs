using System.Text.Json.Serialization;

namespace BoostStudio.Core.Models;

/// <summary>
/// Persistent application state. All values are normalized on load and before use so that a
/// hand-edited or older configuration file can never send the DSP engine out of range.
/// </summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = AppInfo.SettingsSchemaVersion;

    /// <summary>Master processing switch. False writes a bypass include; module state is retained.</summary>
    public bool ProcessingEnabled { get; set; } = true;

    /// <summary>
    /// Reserves headroom for positive EQ and loudness filter gain. Bass Booster is
    /// excluded unless <see cref="BassSettings.ProtectHeadroom"/> is enabled, so enabling
    /// bass does not permanently attenuate mids and highs.
    /// </summary>
    public bool AutoHeadroom { get; set; } = true;

    public BassSettings Bass { get; set; } = new();

    public VolumeSettings Volume { get; set; } = new();

    public EqualizerSettings Equalizer { get; set; } = new();

    public AudioSettings Audio { get; set; } = new();

    public ApplicationSettings Application { get; set; } = new();

    public HotkeySettings Hotkeys { get; set; } = new();

    /// <summary>Optional user-selected Equalizer APO installation or config directory.</summary>
    public string? EqualizerApoPathOverride { get; set; }

    /// <summary>Last page key, used to restore navigation position.</summary>
    public string LastPage { get; set; } = "dashboard";

    [JsonIgnore]
    public bool AnyProcessingEnabled =>
        ProcessingEnabled && (Bass.Enabled || Volume.Enabled || Equalizer.Enabled);

    public void Normalize()
    {
        var previousSchema = SchemaVersion;

        Bass ??= new BassSettings();
        Volume ??= new VolumeSettings();
        Equalizer ??= new EqualizerSettings();
        Audio ??= new AudioSettings();
        Application ??= new ApplicationSettings();
        Hotkeys ??= new HotkeySettings();

        // Schema 2 made the loudness contour opt-in. V1 files always wrote the old
        // implicit default of 20 dB, so migrate that exact value to neutral. An
        // explicit 20 dB written under schema 2 or later is preserved.
        if (previousSchema < 2 && Volume.Loudness == 20)
            Volume.Loudness = 0;

        Bass.Normalize();
        Volume.Normalize();
        Equalizer.Normalize();
        Audio.Normalize();
        Application.Normalize();
        Hotkeys.Normalize();

        SchemaVersion = AppInfo.SettingsSchemaVersion;

        if (LastPage is not ("dashboard" or "bass" or "volume" or "equalizer" or "profiles" or "settings"))
            LastPage = "dashboard";

        if (!string.IsNullOrWhiteSpace(EqualizerApoPathOverride))
            EqualizerApoPathOverride = EqualizerApoPathOverride.Trim();
        else
            EqualizerApoPathOverride = null;
    }

    public static AppSettings CreateDefault()
    {
        var settings = new AppSettings();
        settings.Normalize();
        return settings;
    }

    /// <summary>
    /// Deep copy for background work. Reading the live settings from a worker thread while
    /// the UI edits them can otherwise produce a mixed, half-updated DSP configuration.
    /// The source is not normalized or modified.
    /// </summary>
    public AppSettings Clone()
    {
        var copy = new AppSettings
        {
            SchemaVersion = SchemaVersion,
            ProcessingEnabled = ProcessingEnabled,
            AutoHeadroom = AutoHeadroom,
            EqualizerApoPathOverride = EqualizerApoPathOverride,
            LastPage = LastPage
        };

        copy.Bass = new BassSettings();
        copy.Bass.CopyFrom(Bass);
        copy.Volume = new VolumeSettings();
        copy.Volume.CopyFrom(Volume);
        copy.Equalizer = new EqualizerSettings();
        copy.Equalizer.CopyFrom(Equalizer);
        copy.Audio = new AudioSettings();
        copy.Audio.CopyFrom(Audio);
        copy.Application = new ApplicationSettings();
        copy.Application.CopyFrom(Application);
        copy.Hotkeys = new HotkeySettings();
        copy.Hotkeys.CopyFrom(Hotkeys);
        return copy;
    }

    /// <summary>Copies values into the existing object graph, preserving view-model references.</summary>
    public void CopyFrom(AppSettings other)
    {
        other.Normalize();
        SchemaVersion = other.SchemaVersion;
        ProcessingEnabled = other.ProcessingEnabled;
        AutoHeadroom = other.AutoHeadroom;
        Bass.CopyFrom(other.Bass);
        Volume.CopyFrom(other.Volume);
        Equalizer.CopyFrom(other.Equalizer);
        Audio.CopyFrom(other.Audio);
        Application.CopyFrom(other.Application);
        Hotkeys.CopyFrom(other.Hotkeys);
        EqualizerApoPathOverride = other.EqualizerApoPathOverride;
        LastPage = other.LastPage;
    }
}

public sealed class BassSettings
{
    public bool Enabled { get; set; }

    /// <summary>0-100%. Maps to the adjustable maximum shelf gain.</summary>
    public int Intensity { get; set; } = 45;

    /// <summary>Low-shelf center/corner frequency, 20-250 Hz.</summary>
    public double FrequencyHz { get; set; } = 80;

    /// <summary>Maximum shelf gain at 100% intensity, 0-24 dB.</summary>
    public double MaxGainDb { get; set; } = 18;

    /// <summary>Equalizer APO low-shelf Q (0.3-1.4). Lower = broader, higher = more upper-bass bite.</summary>
    public double ShelfQ { get; set; } = 0.707;

    /// <summary>
    /// When false (default), Bass Booster is volume-preserving: the bass shelf is boosted
    /// without a broadband preamp cut, so mids and highs keep their original level.
    /// When true, auto headroom also reserves the shelf's full static gain, which prevents
    /// clipping but lowers the entire output by that amount (the old behavior).
    /// </summary>
    public bool ProtectHeadroom { get; set; }

    public string PresetName { get; set; } = BassPresets.Custom;

    [JsonIgnore]
    public double EffectiveGainDb => Math.Round(MaxGainDb * Math.Clamp(Intensity, 0, 100) / 100.0, 2);

    public void Normalize()
    {
        Intensity = Math.Clamp(Intensity, 0, 100);
        FrequencyHz = Math.Clamp(FrequencyHz, 20, 250);
        MaxGainDb = Math.Clamp(MaxGainDb, 0, 24);
        ShelfQ = Math.Clamp(ShelfQ, 0.30, 1.40);
        if (string.IsNullOrWhiteSpace(PresetName))
            PresetName = BassPresets.Custom;
    }

    public void CopyFrom(BassSettings other)
    {
        Enabled = other.Enabled;
        Intensity = other.Intensity;
        FrequencyHz = other.FrequencyHz;
        MaxGainDb = other.MaxGainDb;
        ShelfQ = other.ShelfQ;
        ProtectHeadroom = other.ProtectHeadroom;
        PresetName = other.PresetName;
    }
}

public sealed class VolumeSettings
{
    public bool Enabled { get; set; }

    /// <summary>0-100%. Maps to the gain ceiling.</summary>
    public int BoostPercent { get; set; } = 30;

    /// <summary>Maximum digital boost gain, 3-24 dB.</summary>
    public double CeilingDb { get; set; } = 12;

    /// <summary>User preamp trim added to the boost, -12 to +12 dB.</summary>
    public double PreampDb { get; set; }

    /// <summary>Optional static loudness contour (low + high shelf), 0-100.</summary>
    public int Loudness { get; set; } = 0;

    /// <summary>Peak-based slow automatic gain control using real loopback output measurements.</summary>
    public bool AutoGainControl { get; set; }

    [JsonIgnore]
    public double EffectiveBoostDb => Math.Round(CeilingDb * Math.Clamp(BoostPercent, 0, 100) / 100.0, 2);

    public void Normalize()
    {
        BoostPercent = Math.Clamp(BoostPercent, 0, 100);
        CeilingDb = Math.Clamp(CeilingDb, 3, 24);
        PreampDb = Math.Clamp(PreampDb, -12, 12);
        Loudness = Math.Clamp(Loudness, 0, 100);
    }

    public void CopyFrom(VolumeSettings other)
    {
        Enabled = other.Enabled;
        BoostPercent = other.BoostPercent;
        CeilingDb = other.CeilingDb;
        PreampDb = other.PreampDb;
        Loudness = other.Loudness;
        AutoGainControl = other.AutoGainControl;
    }
}

public sealed class EqualizerSettings
{
    public bool Enabled { get; set; }

    /// <summary>Gain in dB for each of the ten ISO octave bands, -12 to +12.</summary>
    public double[] GainsDb { get; set; } = new double[EqConstants.BandCount];

    public string SelectedPresetName { get; set; } = "Balanced";

    public void Normalize()
    {
        var normalized = new double[EqConstants.BandCount];
        for (var i = 0; i < EqConstants.BandCount; i++)
        {
            var value = GainsDb is not null && i < GainsDb.Length ? GainsDb[i] : 0;
            normalized[i] = Math.Round(Math.Clamp(value, -12, 12), 1);
        }

        GainsDb = normalized;
        if (string.IsNullOrWhiteSpace(SelectedPresetName))
            SelectedPresetName = "Custom";
    }

    public void CopyFrom(EqualizerSettings other)
    {
        Enabled = other.Enabled;
        GainsDb = (double[])other.GainsDb.Clone();
        SelectedPresetName = other.SelectedPresetName;
    }
}

public sealed class AudioSettings
{
    /// <summary>Null means follow the current Windows default render device for monitoring.</summary>
    public string? MonitoringDeviceId { get; set; }

    public bool MonitorDefaultDevice { get; set; } = true;

    /// <summary>When true, no Device line is written, so Equalizer APO applies to every APO-enabled output.</summary>
    public bool ApplyToAllDevices { get; set; } = true;

    public string? ScopedDeviceId { get; set; }

    public string? ScopedDevicePattern { get; set; }

    public void Normalize()
    {
        if (MonitorDefaultDevice)
            MonitoringDeviceId = null;
        if (ApplyToAllDevices)
        {
            ScopedDeviceId = null;
            ScopedDevicePattern = null;
        }
        else if (string.IsNullOrWhiteSpace(ScopedDevicePattern))
        {
            ApplyToAllDevices = true;
            ScopedDeviceId = null;
            ScopedDevicePattern = null;
        }
        else
        {
            ScopedDevicePattern = ScopedDevicePattern.Trim();
        }
    }

    public void CopyFrom(AudioSettings other)
    {
        MonitoringDeviceId = other.MonitoringDeviceId;
        MonitorDefaultDevice = other.MonitorDefaultDevice;
        ApplyToAllDevices = other.ApplyToAllDevices;
        ScopedDeviceId = other.ScopedDeviceId;
        ScopedDevicePattern = other.ScopedDevicePattern;
    }
}

public sealed class ApplicationSettings
{
    public bool StartWithWindows { get; set; }

    public bool StartMinimized { get; set; }

    public bool MinimizeToTray { get; set; } = true;

    public bool CloseToTray { get; set; } = true;

    public bool ShowTrayNotifications { get; set; } = true;

    public bool ShowClipWarnings { get; set; } = true;

    /// <summary>Set once the first-run Equalizer APO introduction has been shown.</summary>
    public bool HasSeenWelcome { get; set; }

    /// <summary>Last window width in device-independent pixels. Restored on next launch.</summary>
    public double WindowWidth { get; set; } = 1420;

    /// <summary>Last window height in device-independent pixels. Restored on next launch.</summary>
    public double WindowHeight { get; set; } = 900;

    /// <summary>Last window left position; null means center on the primary display.</summary>
    public double? WindowLeft { get; set; }

    /// <summary>Last window top position; null means center on the primary display.</summary>
    public double? WindowTop { get; set; }

    public bool WindowMaximized { get; set; }

    public void Normalize()
    {
        if (double.IsNaN(WindowWidth) || double.IsInfinity(WindowWidth) || WindowWidth < 1080)
            WindowWidth = 1420;
        else if (WindowWidth > 7680)
            WindowWidth = 7680;

        if (double.IsNaN(WindowHeight) || double.IsInfinity(WindowHeight) || WindowHeight < 690)
            WindowHeight = 900;
        else if (WindowHeight > 4320)
            WindowHeight = 4320;

        if (WindowLeft is null || double.IsNaN(WindowLeft.Value) ||
            double.IsInfinity(WindowLeft.Value) || WindowLeft < -10000 || WindowLeft > 10000)
            WindowLeft = null;

        if (WindowTop is null || double.IsNaN(WindowTop.Value) ||
            double.IsInfinity(WindowTop.Value) || WindowTop < -10000 || WindowTop > 10000)
            WindowTop = null;
    }

    public void CopyFrom(ApplicationSettings other)
    {
        StartWithWindows = other.StartWithWindows;
        StartMinimized = other.StartMinimized;
        MinimizeToTray = other.MinimizeToTray;
        CloseToTray = other.CloseToTray;
        ShowTrayNotifications = other.ShowTrayNotifications;
        ShowClipWarnings = other.ShowClipWarnings;
        HasSeenWelcome = other.HasSeenWelcome;
        WindowWidth = other.WindowWidth;
        WindowHeight = other.WindowHeight;
        WindowLeft = other.WindowLeft;
        WindowTop = other.WindowTop;
        WindowMaximized = other.WindowMaximized;
    }
}

public sealed class HotkeySettings
{
    public bool Enabled { get; set; } = true;

    public string ToggleBass { get; set; } = "Ctrl+Alt+B";

    public string ToggleVolume { get; set; } = "Ctrl+Alt+V";

    public string ToggleProcessing { get; set; } = "Ctrl+Alt+M";

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(ToggleBass)) ToggleBass = "Ctrl+Alt+B";
        if (string.IsNullOrWhiteSpace(ToggleVolume)) ToggleVolume = "Ctrl+Alt+V";
        if (string.IsNullOrWhiteSpace(ToggleProcessing)) ToggleProcessing = "Ctrl+Alt+M";
    }

    public void CopyFrom(HotkeySettings other)
    {
        Enabled = other.Enabled;
        ToggleBass = other.ToggleBass;
        ToggleVolume = other.ToggleVolume;
        ToggleProcessing = other.ToggleProcessing;
    }
}

public static class AppInfo
{
    public const string ProductName = "Boost Studio";
    public const string Version = "1.1.1";
    public const int SettingsSchemaVersion = 2;
    public const string Tagline = "System-wide sound shaping";

    public const string EqualizerApoDownloadUrl = "https://sourceforge.net/projects/equalizerapo/files/latest/download";
    public const string EqualizerApoProjectUrl = "https://sourceforge.net/projects/equalizerapo/";
    public const string EqualizerApoDocsUrl = "https://sourceforge.net/p/equalizerapo/wiki/Configuration%20reference";
}

public static class EqConstants
{
    public const int BandCount = 10;

    public const double Q = 1.41;

    public static readonly double[] BandFrequencies = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

    public static readonly string[] BandLabels = { "31", "62", "125", "250", "500", "1k", "2k", "4k", "8k", "16k" };

    public static string FrequencyLabel(int index) =>
        index >= 0 && index < BandLabels.Length ? BandLabels[index] : string.Empty;
}
