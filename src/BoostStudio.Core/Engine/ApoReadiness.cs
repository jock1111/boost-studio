using BoostStudio.Core.Audio;
using BoostStudio.Core.Models;
using BoostStudio.Core.Storage;

namespace BoostStudio.Core.Engine;

/// <summary>
/// Evidence collected from Windows about whether Equalizer APO is attached to an output.
/// This is association evidence only: it never proves that audio was processed audibly.
/// </summary>
public sealed record ApoAssessment(
    bool Checked,
    int DeviceCount,
    int ApoEnabledDeviceCount,
    bool TargetApoEnabled,
    string Scope,
    string? TargetDeviceName)
{
    public static ApoAssessment Unavailable(string scope) =>
        new(false, 0, 0, false, scope, null);

    public bool AnyApoEnabled => ApoEnabledDeviceCount > 0;
}

/// <summary>Test seam and extension point for APO association checks.</summary>
public interface IApoReadinessProbe
{
    ApoAssessment Evaluate(AudioSettings audio);
}

/// <summary>
/// Enumerates Windows render endpoints and inspects each endpoint's FxProperties for
/// Equalizer APO's APO CLSID. For "all devices" scope the target is considered enabled
/// when at least one output has the APO registered; for a scoped output the selected
/// endpoint itself must have the APO registered.
/// </summary>
public sealed class AudioDeviceApoReadinessProbe : IApoReadinessProbe
{
    private readonly AudioDeviceService _devices;
    private readonly ILogService? _log;

    public AudioDeviceApoReadinessProbe(ILogService? log = null)
    {
        _devices = new AudioDeviceService(log);
        _log = log;
    }

    public ApoAssessment Evaluate(AudioSettings audio)
    {
        var scope = audio.ApplyToAllDevices
            ? "All outputs"
            : (string.IsNullOrWhiteSpace(audio.ScopedDevicePattern) ? "Selected output" : audio.ScopedDevicePattern);

        try
        {
            var devices = _devices.GetRenderDevices();
            if (devices.Count == 0)
                return ApoAssessment.Unavailable(scope);

            var enabled = devices.Count(d => d.ApoAssociated);

            if (audio.ApplyToAllDevices)
            {
                return new ApoAssessment(true, devices.Count, enabled, enabled > 0,
                    "All APO-enabled outputs", null);
            }

            var target = devices.FirstOrDefault(d => Matches(d, audio));
            return new ApoAssessment(
                true,
                devices.Count,
                enabled,
                target?.ApoAssociated ?? false,
                target?.Name ?? scope,
                target?.Name);
        }
        catch (Exception ex)
        {
            _log?.Warn($"Could not inspect Equalizer APO device association: {ex.Message}");
            return ApoAssessment.Unavailable(scope);
        }
    }

    /// <summary>
    /// The scope pattern is a list of tokens generated from the device and endpoint names,
    /// mirroring Equalizer APO's Device command. An exact endpoint ID always wins.
    /// </summary>
    private static bool Matches(AudioDeviceInfo device, AudioSettings audio)
    {
        if (!string.IsNullOrWhiteSpace(audio.ScopedDeviceId) &&
            string.Equals(device.Id, audio.ScopedDeviceId, StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.IsNullOrWhiteSpace(audio.ScopedDevicePattern))
            return false;

        var haystack = $"{device.DeviceName} {device.Name} {device.Id}";
        var tokens = audio.ScopedDevicePattern.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length > 0 &&
               tokens.All(token => haystack.Contains(token, StringComparison.OrdinalIgnoreCase));
    }
}
