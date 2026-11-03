namespace BoostStudio.Core.Models;

public sealed record EqBuiltInPreset(string Name, string Description, double[] GainsDb);

public static class EqPresets
{
    public const string Custom = "Custom";

    public static readonly IReadOnlyList<EqBuiltInPreset> All = new[]
    {
        new EqBuiltInPreset("Balanced", "Neutral response with gentle presence",
            new[] { 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 }),

        new EqBuiltInPreset("Music", "Warm low end, open upper mids",
            new[] { 1.5, 2.0, 1.0, -0.5, 0.5, 1.0, 0.5, 1.0, 1.5, 1.0 }),

        new EqBuiltInPreset("Movies", "Impact and dialogue clarity",
            new[] { 3.0, 2.5, 1.0, -1.0, 0.0, 1.0, 1.5, 1.0, 0.0, -1.0 }),

        new EqBuiltInPreset("Gaming", "Footsteps and cues without mud",
            new[] { 0.0, 1.0, 0.5, -1.5, -1.0, 1.0, 3.0, 2.5, 1.0, 0.0 }),

        new EqBuiltInPreset("EDM", "Club sub and bright energy",
            new[] { 4.0, 3.0, 1.0, -1.0, -1.0, 0.0, 1.0, 2.0, 2.0, 1.5 }),

        new EqBuiltInPreset("Hip-Hop", "Sub weight with vocal edge",
            new[] { 4.0, 3.5, 2.0, 0.0, -1.0, 0.0, 0.5, 1.5, 1.0, 0.5 }),

        new EqBuiltInPreset("Rock", "Guitar body and drum punch",
            new[] { 2.0, 1.5, 0.0, -1.0, -0.5, 1.0, 2.0, 2.5, 1.5, 0.5 }),

        new EqBuiltInPreset("Vocal", "Forward speech and vocals",
            new[] { -2.0, -1.5, 0.0, 1.0, 2.0, 2.5, 2.0, 1.0, 0.0, -1.0 })
    };

    public static EqBuiltInPreset? Find(string? name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public static bool Matches(double[] gains, EqBuiltInPreset preset)
    {
        if (gains.Length != preset.GainsDb.Length) return false;
        for (var i = 0; i < gains.Length; i++)
        {
            if (Math.Abs(gains[i] - preset.GainsDb[i]) > 0.05)
                return false;
        }

        return true;
    }

    public static string Detect(double[] gains)
    {
        foreach (var preset in All)
        {
            if (Matches(gains, preset))
                return preset.Name;
        }

        return Custom;
    }
}

public sealed class UserEqPreset
{
    public string Name { get; set; } = "My preset";

    public double[] GainsDb { get; set; } = new double[EqConstants.BandCount];

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;

    public UserEqPreset Clone() => new()
    {
        Name = Name,
        GainsDb = (double[])GainsDb.Clone(),
        CreatedUtc = CreatedUtc,
        ModifiedUtc = ModifiedUtc
    };
}

public sealed class UserPresetLibrary
{
    public int SchemaVersion { get; set; } = 1;

    public List<UserEqPreset> EqualizerPresets { get; set; } = new();

    public void Normalize()
    {
        EqualizerPresets ??= new List<UserEqPreset>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cleaned = new List<UserEqPreset>();

        foreach (var preset in EqualizerPresets)
        {
            if (preset is null || string.IsNullOrWhiteSpace(preset.Name))
                continue;

            preset.Name = preset.Name.Trim();
            if (preset.Name.Length > 40)
                preset.Name = preset.Name[..40];
            if (!seen.Add(preset.Name))
                continue;

            var gains = new double[EqConstants.BandCount];
            for (var i = 0; i < gains.Length; i++)
                gains[i] = Math.Round(Math.Clamp(preset.GainsDb is not null && i < preset.GainsDb.Length ? preset.GainsDb[i] : 0, -12, 12), 1);

            preset.GainsDb = gains;
            if (preset.CreatedUtc == default) preset.CreatedUtc = DateTime.UtcNow;
            if (preset.ModifiedUtc == default) preset.ModifiedUtc = preset.CreatedUtc;
            cleaned.Add(preset);
        }

        EqualizerPresets = cleaned;
    }
}
