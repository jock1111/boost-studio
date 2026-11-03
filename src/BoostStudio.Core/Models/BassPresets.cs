namespace BoostStudio.Core.Models;

public sealed record BassPreset(
    string Name,
    string ShortName,
    int Intensity,
    double FrequencyHz,
    double MaxGainDb,
    double ShelfQ,
    string Description);

public static class BassPresets
{
    public const string Custom = "Custom";

    public static readonly IReadOnlyList<BassPreset> All = new[]
    {
        new BassPreset("Deep Bass", "Deep", 55, 55, 20, 0.55,
            "A broad, room-filling sub foundation with a natural roll-off."),
        new BassPreset("Punchy Bass", "Punchy", 65, 105, 18, 1.20,
            "Focused upper-bass impact for kick drums, games and electronic music."),
        new BassPreset("Sub-Bass", "Sub", 72, 42, 22, 0.40,
            "Very low extension for subwoofers and capable headphones."),
        new BassPreset("Extreme Bass", "Extreme", 100, 75, 24, 0.75,
            "Maximum bass energy. Auto headroom is strongly recommended."),
        new BassPreset(Custom, "Custom", 45, 80, 18, 0.707,
            "Your own low-shelf settings.")
    };

    public static BassPreset? Find(string? name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public static bool Matches(BassSettings settings, BassPreset preset)
    {
        if (preset.Name == Custom) return false;
        return settings.Intensity == preset.Intensity
               && Math.Abs(settings.FrequencyHz - preset.FrequencyHz) < 0.01
               && Math.Abs(settings.MaxGainDb - preset.MaxGainDb) < 0.01
               && Math.Abs(settings.ShelfQ - preset.ShelfQ) < 0.001;
    }

    /// <summary>Returns the matching named preset or "Custom".</summary>
    public static string DetectPreset(BassSettings settings)
    {
        foreach (var preset in All)
        {
            if (preset.Name != Custom && Matches(settings, preset))
                return preset.Name;
        }

        return Custom;
    }

    public static void Apply(BassSettings settings, BassPreset preset)
    {
        settings.Intensity = preset.Intensity;
        settings.FrequencyHz = preset.FrequencyHz;
        settings.MaxGainDb = preset.MaxGainDb;
        settings.ShelfQ = preset.ShelfQ;
        settings.PresetName = preset.Name;
    }
}
