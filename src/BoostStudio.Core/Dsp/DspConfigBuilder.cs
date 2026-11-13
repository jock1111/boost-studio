using System.Globalization;
using System.Text;
using BoostStudio.Core.Models;

namespace BoostStudio.Core.Dsp;

public sealed record DspMetrics(
    double PreampDb,
    double HeadroomReservedDb,
    double StaticMaxBoostDb,
    double VolumeGainDb,
    double RuntimeAgcTrimDb,
    int FilterCount,
    bool BassActive,
    bool VolumeActive,
    bool EqualizerActive,
    bool Bypassed);

public sealed record DspConfiguration(string Text, DspMetrics Metrics);

/// <summary>
/// Generates the Equalizer APO include file. This is real DSP configuration:
/// a low-shelf filter for Bass Booster, a digital preamp for Volume Booster,
/// ten peaking biquads for EQ and optional loudness shelves.
/// </summary>
public sealed class DspConfigBuilder
{
    public DspConfiguration Build(AppSettings settings, double runtimeAgcTrimDb = 0, string? devicePattern = null)
    {
        settings.Normalize();
        runtimeAgcTrimDb = Math.Clamp(runtimeAgcTrimDb, -24, 0);

        if (!settings.ProcessingEnabled)
        {
            return BypassConfiguration(settings);
        }

        var bassActive = settings.Bass.Enabled && settings.Bass.EffectiveGainDb >= 0.01;
        var volumeActive = settings.Volume.Enabled;
        var eqActive = settings.Equalizer.Enabled && settings.Equalizer.GainsDb.Any(g => Math.Abs(g) >= 0.05);
        var anyActive = bassActive || volumeActive || eqActive;

        var headroom = DspMath.ComputeHeadroomReservationDb(settings);
        var headroomBoost = DspMath.ComputeHeadroomBoostDb(settings);
        var staticBoost = DspMath.ComputeMaxStaticBoostDb(settings);
        var volumeGain = volumeActive ? settings.Volume.EffectiveBoostDb + settings.Volume.PreampDb : 0;
        var preamp = Math.Round(Math.Clamp(volumeGain + runtimeAgcTrimDb - headroom, -60, 30), 1);

        var builder = new StringBuilder(2048);
        builder.AppendLine("# =====================================================================");
        builder.AppendLine("#  BOOST STUDIO DSP CONFIGURATION");
        builder.AppendLine("#  Managed file. Changes are overwritten by Boost Studio.");
        builder.AppendLine($"#  Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        builder.AppendLine("# =====================================================================");

        if (!anyActive)
        {
            builder.AppendLine();
            builder.AppendLine("# No processing modules are enabled. Audio is left unprocessed by Boost Studio.");
            builder.AppendLine("# Module settings remain saved and are restored when a module is enabled.");
            if (settings.AutoHeadroom) builder.AppendLine("# Auto headroom is idle.");
            return new DspConfiguration(builder.ToString(), new DspMetrics(
                0, 0, 0, 0, 0, 0, false, false, false, false));
        }

        if (!string.IsNullOrWhiteSpace(devicePattern))
        {
            builder.AppendLine();
            builder.AppendLine("# Processing is scoped to this output device (best-effort matching).");
            builder.AppendLine($"Device: {devicePattern}");
        }

        builder.AppendLine();
        builder.AppendLine("# Equalizer APO applies these filters to all channels at the post-mix stage.");
        builder.AppendLine("Channel: all");

        builder.AppendLine();
        builder.AppendLine("# Combined preamp: volume boost + user trim + runtime AGC - managed headroom.");
        builder.AppendLine("# Bass is volume-preserving by default: its shelf gain is not subtracted from");
        builder.AppendLine("# the preamp, so turning Bass Booster on does not lower mids and highs.");
        builder.AppendLine($"Preamp: {preamp.ToString("0.0", CultureInfo.InvariantCulture)} dB");

        var filterIndex = 0;

        if (bassActive)
        {
            var bass = settings.Bass;
            builder.AppendLine();
            builder.AppendLine($"# Bass Booster: {bass.PresetName} - low shelf, {bass.EffectiveGainDb:0.0} dB at {bass.FrequencyHz:0.#} Hz.");
            builder.AppendLine(bass.ProtectHeadroom
                ? "# Safe headroom mode: the shelf gain is also reserved in the preamp, so the whole output is lowered to guarantee no static clipping."
                : "# Volume-preserving mode: the shelf is boosted without a broadband preamp cut. Near-full-scale bass can clip; enable Protect headroom for a static guarantee.");
            builder.AppendLine($"Filter {++filterIndex}: ON LS Fc {bass.FrequencyHz.ToString("0.#", CultureInfo.InvariantCulture)} Hz Gain {bass.EffectiveGainDb.ToString("0.0", CultureInfo.InvariantCulture)} dB Q {bass.ShelfQ.ToString("0.000", CultureInfo.InvariantCulture)}");
        }

        if (eqActive)
        {
            builder.AppendLine();
            builder.AppendLine($"# Equalizer: {settings.Equalizer.SelectedPresetName}, 10-band peaking filters.");
            for (var i = 0; i < EqConstants.BandCount; i++)
            {
                var gain = settings.Equalizer.GainsDb[i];
                if (Math.Abs(gain) < 0.05) continue;
                builder.AppendLine($"Filter {++filterIndex}: ON PK Fc {EqConstants.BandFrequencies[i].ToString("0.#", CultureInfo.InvariantCulture)} Hz Gain {gain.ToString("0.0", CultureInfo.InvariantCulture)} dB Q {EqConstants.Q.ToString("0.00", CultureInfo.InvariantCulture)}");
            }
        }

        var loudnessFilters = DspMath.BuildLoudnessFilters(settings.Volume);
        if (loudnessFilters.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine($"# Loudness contour: {settings.Volume.Loudness}% (static low/high shelf, separate from Bass Booster).");
            if (loudnessFilters.Any(f => f.Kind == "LS") && settings.Volume.Loudness > 0)
            {
                var lowGain = 4.0 * settings.Volume.Loudness / 100.0;
                builder.AppendLine($"Filter {++filterIndex}: ON LS Fc 110 Hz Gain {lowGain.ToString("0.00", CultureInfo.InvariantCulture)} dB Q 0.707");
            }
            if (settings.Volume.Loudness > 0)
            {
                var highGain = 2.5 * settings.Volume.Loudness / 100.0;
                builder.AppendLine($"Filter {++filterIndex}: ON HS Fc 9000 Hz Gain {highGain.ToString("0.00", CultureInfo.InvariantCulture)} dB Q 0.707");
            }
        }

        builder.AppendLine();
        if (settings.AutoHeadroom && headroom > 0)
        {
            var scope = settings.Bass.ProtectHeadroom ? "bass + EQ/loudness" : "EQ/loudness";
            builder.AppendLine($"# Auto headroom reserved: -{headroom:0.0} dB ({scope} boost {headroomBoost:0.0} dB + safety margin).");
        }
        else if (!settings.AutoHeadroom)
            builder.AppendLine("# Auto headroom is disabled. Watch the clipping indicator when boosting filters.");

        if (bassActive && !settings.Bass.ProtectHeadroom)
            builder.AppendLine($"# Total static boost including bass is {staticBoost:0.0} dB; the bass part is intentionally not attenuated.");

        if (volumeActive)
        {
            builder.AppendLine($"# Volume Booster: digital preamp {volumeGain:+0.0;-0.0;0.0} dB ({(int)(settings.Volume.BoostPercent)}% of a {settings.Volume.CeilingDb:0.#} dB ceiling).");
            if (runtimeAgcTrimDb < 0)
                builder.AppendLine($"# Peak AGC runtime trim: {runtimeAgcTrimDb:0.0} dB.");
        }

        builder.AppendLine("# Note: Equalizer APO 1.4.x has no native sample-accurate limiter. Boost Studio");
        builder.AppendLine("#       prevents static clipping for managed filters and offers optional peak AGC.");
        if (bassActive && !settings.Bass.ProtectHeadroom)
            builder.AppendLine("#       Bass is volume-preserving; very hot bass content may clip without safe mode.");
        builder.AppendLine("# =====================================================================");

        var metrics = new DspMetrics(
            preamp,
            headroom,
            staticBoost,
            volumeGain,
            runtimeAgcTrimDb,
            filterIndex,
            bassActive,
            volumeActive,
            eqActive,
            false);

        return new DspConfiguration(builder.ToString(), metrics);
    }

    private static DspConfiguration BypassConfiguration(AppSettings settings)
    {
        var text = new StringBuilder()
            .AppendLine("# =====================================================================")
            .AppendLine("#  BOOST STUDIO DSP CONFIGURATION")
            .AppendLine("#  Managed file. Changes are overwritten by Boost Studio.")
            .AppendLine($"#  Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC")
            .AppendLine("# =====================================================================")
            .AppendLine()
            .AppendLine("# MASTER BYPASS: all Boost Studio processing is disabled.")
            .AppendLine("# Module settings and presets are retained.")
            .AppendLine("# =====================================================================")
            .ToString();

        return new DspConfiguration(text, new DspMetrics(
            0, 0, 0, 0, 0, 0, false, false, false, true));
    }

    /// <summary>Validates every line generated by this builder using the EAPO command grammar subset we use.</summary>
    public static IReadOnlyList<string> Validate(string configText)
    {
        var errors = new List<string>();
        var lineNumber = 0;
        foreach (var raw in configText.Replace("\r\n", "\n").Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            if (line.StartsWith("Preamp:", StringComparison.Ordinal))
            {
                if (!double.TryParse(line[7..].Replace("dB", "", StringComparison.Ordinal).Trim(),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    errors.Add($"Line {lineNumber}: invalid Preamp value.");
                continue;
            }

            if (line.StartsWith("Filter ", StringComparison.Ordinal) || line.StartsWith("Filter:", StringComparison.Ordinal))
            {
                if (!line.Contains(" Fc ", StringComparison.Ordinal) ||
                    !line.Contains(" Hz", StringComparison.Ordinal))
                {
                    errors.Add($"Line {lineNumber}: filter is missing Fc/Hz.");
                }
                else if (!line.Contains(" ON ", StringComparison.Ordinal))
                {
                    errors.Add($"Line {lineNumber}: filter is missing ON state.");
                }
                continue;
            }

            if (line.StartsWith("Device:", StringComparison.Ordinal) ||
                line.StartsWith("Channel:", StringComparison.Ordinal))
                continue;

            errors.Add($"Line {lineNumber}: unsupported command '{line.Split(' ')[0]}'.");
        }

        return errors;
    }
}
