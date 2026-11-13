using System.Text.Json.Serialization;

namespace BoostStudio.Core.Models;

/// <summary>
/// A complete, named sound setup: master switch, auto headroom, Bass Booster,
/// Volume Booster and Equalizer. Application preferences, hotkeys and device
/// routing are deliberately excluded so applying a profile never changes how the
/// app behaves or which output is processed.
/// </summary>
public sealed class SoundProfile
{
    public const int MaxNameLength = 48;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "New profile";

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;

    public bool ProcessingEnabled { get; set; } = true;

    public bool AutoHeadroom { get; set; } = true;

    public BassSettings Bass { get; set; } = new();

    public VolumeSettings Volume { get; set; } = new();

    public EqualizerSettings Equalizer { get; set; } = new();

    [JsonIgnore]
    public int ActiveModuleCount =>
        (Bass.Enabled ? 1 : 0) + (Volume.Enabled ? 1 : 0) + (Equalizer.Enabled ? 1 : 0);

    [JsonIgnore]
    public string ModuleSummary
    {
        get
        {
            if (!ProcessingEnabled) return "Master bypassed";
            if (ActiveModuleCount == 0) return "Standby · no modules enabled";

            var parts = new List<string>();
            if (Bass.Enabled)
                parts.Add($"Bass {Bass.EffectiveGainDb:+0.0;-0.0;0.0} dB");
            if (Volume.Enabled)
                parts.Add($"Volume {Volume.EffectiveBoostDb:+0.0;-0.0;0.0} dB");
            if (Equalizer.Enabled)
                parts.Add($"EQ {Equalizer.SelectedPresetName}");
            return string.Join("  ·  ", parts);
        }
    }

    [JsonIgnore]
    public string DetailSummary
    {
        get
        {
            var parts = new List<string>();
            if (Bass.Enabled)
                parts.Add($"Low shelf {Bass.EffectiveGainDb:0.#} dB @ {Bass.FrequencyHz:0.#} Hz");
            if (Volume.Enabled)
                parts.Add($"Volume {Volume.BoostPercent}% of {Volume.CeilingDb:0.#} dB");
            if (Equalizer.Enabled)
                parts.Add($"Equalizer “{Equalizer.SelectedPresetName}”");
            if (parts.Count == 0) parts.Add("No processing modules enabled");
            if (AutoHeadroom) parts.Add("Auto headroom on");
            return string.Join(" · ", parts);
        }
    }

    public static SoundProfile Capture(string name, AppSettings settings)
    {
        var profile = new SoundProfile
        {
            Name = NormalizeName(name),
            CreatedUtc = DateTime.UtcNow,
            ModifiedUtc = DateTime.UtcNow
        };
        profile.CaptureFrom(settings);
        return profile;
    }

    /// <summary>Copies only the audio processing state from the live settings.</summary>
    public void CaptureFrom(AppSettings settings)
    {
        ProcessingEnabled = settings.ProcessingEnabled;
        AutoHeadroom = settings.AutoHeadroom;
        Bass.CopyFrom(settings.Bass);
        Volume.CopyFrom(settings.Volume);
        Equalizer.CopyFrom(settings.Equalizer);
    }

    /// <summary>Applies only the audio processing state, leaving app preferences intact.</summary>
    public void ApplyTo(AppSettings settings)
    {
        settings.ProcessingEnabled = ProcessingEnabled;
        settings.AutoHeadroom = AutoHeadroom;
        settings.Bass.CopyFrom(Bass);
        settings.Volume.CopyFrom(Volume);
        settings.Equalizer.CopyFrom(Equalizer);
        settings.Normalize();
    }

    public void UpdateFrom(AppSettings settings)
    {
        CaptureFrom(settings);
        ModifiedUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// True when the live settings would produce the same processing as this
    /// profile. Preset names and timestamps are metadata and are ignored.
    /// </summary>
    public bool Matches(AppSettings settings)
    {
        if (settings.ProcessingEnabled != ProcessingEnabled ||
            settings.AutoHeadroom != AutoHeadroom)
            return false;

        if (settings.Bass.Enabled != Bass.Enabled ||
            settings.Bass.Intensity != Bass.Intensity ||
            Math.Abs(settings.Bass.FrequencyHz - Bass.FrequencyHz) > 0.01 ||
            Math.Abs(settings.Bass.MaxGainDb - Bass.MaxGainDb) > 0.01 ||
            Math.Abs(settings.Bass.ShelfQ - Bass.ShelfQ) > 0.001 ||
            settings.Bass.ProtectHeadroom != Bass.ProtectHeadroom)
            return false;

        if (settings.Volume.Enabled != Volume.Enabled ||
            settings.Volume.BoostPercent != Volume.BoostPercent ||
            Math.Abs(settings.Volume.CeilingDb - Volume.CeilingDb) > 0.01 ||
            Math.Abs(settings.Volume.PreampDb - Volume.PreampDb) > 0.01 ||
            settings.Volume.Loudness != Volume.Loudness ||
            settings.Volume.AutoGainControl != Volume.AutoGainControl)
            return false;

        if (settings.Equalizer.Enabled != Equalizer.Enabled ||
            settings.Equalizer.GainsDb.Length != Equalizer.GainsDb.Length)
            return false;

        for (var i = 0; i < Equalizer.GainsDb.Length; i++)
        {
            if (Math.Abs(settings.Equalizer.GainsDb[i] - Equalizer.GainsDb[i]) > 0.05)
                return false;
        }

        return true;
    }

    public SoundProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        CreatedUtc = CreatedUtc,
        ModifiedUtc = ModifiedUtc,
        ProcessingEnabled = ProcessingEnabled,
        AutoHeadroom = AutoHeadroom,
        Bass = CopyBass(Bass),
        Volume = CopyVolume(Volume),
        Equalizer = CopyEqualizer(Equalizer)
    };

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(Id) || !Guid.TryParseExact(Id, "N", out _))
            Id = Guid.NewGuid().ToString("N");

        Name = NormalizeName(Name);
        if (CreatedUtc == default) CreatedUtc = DateTime.UtcNow;
        if (ModifiedUtc == default) ModifiedUtc = CreatedUtc;
        if (ModifiedUtc < CreatedUtc) ModifiedUtc = CreatedUtc;

        Bass ??= new BassSettings();
        Volume ??= new VolumeSettings();
        Equalizer ??= new EqualizerSettings();
        Bass.Normalize();
        Volume.Normalize();
        Equalizer.Normalize();
    }

    public static string NormalizeName(string? name)
    {
        var trimmed = string.IsNullOrWhiteSpace(name) ? "Untitled profile" : name.Trim();
        return trimmed.Length > MaxNameLength ? trimmed[..MaxNameLength] : trimmed;
    }

    private static BassSettings CopyBass(BassSettings source)
    {
        var copy = new BassSettings();
        copy.CopyFrom(source);
        return copy;
    }

    private static VolumeSettings CopyVolume(VolumeSettings source)
    {
        var copy = new VolumeSettings();
        copy.CopyFrom(source);
        return copy;
    }

    private static EqualizerSettings CopyEqualizer(EqualizerSettings source)
    {
        var copy = new EqualizerSettings();
        copy.CopyFrom(source);
        return copy;
    }
}

/// <summary>Persisted collection of sound profiles and the last applied profile.</summary>
public sealed class ProfileLibrary
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Id of the profile that was last applied, used for UI and tray checkmarks.</summary>
    public string? ActiveProfileId { get; set; }

    public List<SoundProfile> Profiles { get; set; } = new();

    public SoundProfile? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : Profiles.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public SoundProfile? FindByName(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : Profiles.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    public void Normalize()
    {
        Profiles ??= new List<SoundProfile>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cleaned = new List<SoundProfile>();

        foreach (var profile in Profiles)
        {
            if (profile is null)
                continue;

            profile.Normalize();

            if (!ids.Add(profile.Id))
                profile.Id = Guid.NewGuid().ToString("N");
            ids.Add(profile.Id);

            if (!names.Add(profile.Name))
            {
                // Preserve the profile but make its name distinguishable rather than
                // silently discarding user data from a hand-edited file.
                var baseName = profile.Name;
                var suffix = 2;
                do
                {
                    var tail = $" ({suffix++})";
                    var room = Math.Max(1, SoundProfile.MaxNameLength - tail.Length);
                    profile.Name = (baseName.Length > room ? baseName[..room] : baseName) + tail;
                } while (!names.Add(profile.Name));
            }

            cleaned.Add(profile);
        }

        Profiles = cleaned;
        if (Find(ActiveProfileId) is null)
            ActiveProfileId = null;
    }
}
