using System.Text;
using BoostStudio.Core.Engine;
using BoostStudio.Core.Models;

namespace BoostStudio.Core.Tests;

internal sealed class FixedApoProbe : IApoReadinessProbe
{
    public FixedApoProbe(bool checkedStatus = true, bool targetEnabled = true, int enabledCount = 1)
    {
        Checked = checkedStatus;
        TargetEnabled = targetEnabled;
        EnabledCount = enabledCount;
    }

    public bool Checked { get; }

    public bool TargetEnabled { get; }

    public int EnabledCount { get; }

    public ApoAssessment Evaluate(AudioSettings audio) =>
        new(Checked, 2, EnabledCount, TargetEnabled, "Test output", TargetEnabled ? "Test output" : null);
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BoostStudio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public string Subdirectory(string name)
    {
        var path = System.IO.Path.Combine(Path, name);
        System.IO.Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (System.IO.Directory.Exists(Path))
                System.IO.Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // Test cleanup is best effort on Windows where audio processes may briefly hold files.
        }
    }
}

internal static class TestSettings
{
    public static AppSettings BassOnly()
    {
        var settings = AppSettings.CreateDefault();
        settings.ProcessingEnabled = true;
        settings.AutoHeadroom = false;
        settings.Bass.Enabled = true;
        settings.Bass.Intensity = 60;
        settings.Bass.FrequencyHz = 80;
        settings.Bass.MaxGainDb = 15;
        settings.Bass.ShelfQ = 0.707;
        settings.Volume.Enabled = false;
        settings.Volume.Loudness = 0;
        settings.Equalizer.Enabled = false;
        return settings;
    }

    public static AppSettings VolumeOnly()
    {
        var settings = AppSettings.CreateDefault();
        settings.ProcessingEnabled = true;
        settings.AutoHeadroom = false;
        settings.Volume.Enabled = true;
        settings.Volume.BoostPercent = 50;
        settings.Volume.CeilingDb = 12;
        settings.Volume.PreampDb = 0;
        settings.Volume.Loudness = 0;
        settings.Bass.Enabled = false;
        settings.Equalizer.Enabled = false;
        return settings;
    }

    public static AppSettings EqualizerOnly()
    {
        var settings = AppSettings.CreateDefault();
        settings.ProcessingEnabled = true;
        settings.AutoHeadroom = false;
        settings.Bass.Enabled = false;
        settings.Volume.Enabled = false;
        settings.Volume.Loudness = 0;
        settings.Equalizer.Enabled = true;
        settings.Equalizer.GainsDb = new double[] { 0, 2, 0, -3, 0, 4, 0, 0, 0, 0 };
        settings.Equalizer.SelectedPresetName = "Test";
        return settings;
    }

    public static string Fingerprint(AppSettings settings) => new StringBuilder()
        .Append(settings.ProcessingEnabled).Append('|')
        .Append(settings.Bass.Enabled).Append('|')
        .Append(settings.Bass.Intensity).Append('|')
        .Append(settings.Volume.Enabled).Append('|')
        .Append(settings.Volume.BoostPercent).Append('|')
        .Append(settings.Equalizer.Enabled).Append('|')
        .AppendJoin(',', settings.Equalizer.GainsDb)
        .ToString();
}
