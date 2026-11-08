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
    string? TargetDeviceName,
    bool DefaultDeviceApoEnabled = false,
    string? DefaultDeviceName = null,
    string? SuggestedApoDeviceName = null,
    string? SuggestedApoDeviceId = null)
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
/// Equalizer APO's APO CLSID. For "all devices" scope the Windows default output is the
/// target, because that is where the user actually hears audio; a scoped configuration
/// targets the selected endpoint itself.
/// </summary>
public sealed class AudioDeviceApoReadinessProbe : IApoReadinessProbe
{
    private readonly AudioDeviceService _devices;
    private readonly Func<IReadOnlyList<AudioDeviceInfo>> _deviceSource;
    private readonly ILogService? _log;

    public AudioDeviceApoReadinessProbe(ILogService? log = null,
        Func<IReadOnlyList<AudioDeviceInfo>>? deviceSource = null)
    {
        _devices = new AudioDeviceService(log);
        _deviceSource = deviceSource ?? _devices.GetRenderDevices;
        _log = log;
    }

    public ApoAssessment Evaluate(AudioSettings audio)
    {
        var scope = audio.ApplyToAllDevices
            ? "All outputs"
            : (string.IsNullOrWhiteSpace(audio.ScopedDevicePattern) ? "Selected output" : audio.ScopedDevicePattern);

        try
        {
            var devices = _deviceSource();
            if (devices.Count == 0)
                return ApoAssessment.Unavailable(scope);

            var enabled = devices.Count(d => d.ApoAssociated);
            var defaultDevice = devices.FirstOrDefault(d => d.IsDefault);
            var defaultEnabled = defaultDevice?.ApoAssociated ?? false;

            if (audio.ApplyToAllDevices)
            {
                // "All devices" means all APO-enabled outputs, but the vast majority of
                // audio is heard on the Windows default endpoint. Reporting a different
                // output as the target would say "ready" while the user's speakers are
                // unprocessed, so the default output is the target for this check.
                var suggestion = defaultEnabled
                    ? null
                    : PickSuggestion(devices.Where(d => d.ApoAssociated), defaultDevice);
                return new ApoAssessment(
                    true,
                    devices.Count,
                    enabled,
                    defaultEnabled,
                    defaultDevice is null ? "the Windows default output" : defaultDevice.Name,
                    defaultDevice?.Name,
                    defaultEnabled,
                    defaultDevice?.Name,
                    suggestion?.Name,
                    suggestion?.Id);
            }

            var target = devices.FirstOrDefault(d => Matches(d, audio));
            var scopedSuggestion = target?.ApoAssociated == true
                ? null
                : PickSuggestion(devices.Where(d => d.ApoAssociated), target);
            return new ApoAssessment(
                true,
                devices.Count,
                enabled,
                target?.ApoAssociated ?? false,
                target?.Name ?? scope,
                target?.Name,
                defaultEnabled,
                defaultDevice?.Name,
                scopedSuggestion?.Name,
                scopedSuggestion?.Id);
        }
        catch (Exception ex)
        {
            _log?.Warn($"Could not inspect Equalizer APO device association: {ex.Message}");
            return ApoAssessment.Unavailable(scope);
        }
    }

    /// <summary>
    /// Picks an output that already has Equalizer APO so the UI can offer a working
    /// alternative instead of only reporting that the current output is unprocessed.
    /// </summary>
    private static AudioDeviceInfo? PickSuggestion(IEnumerable<AudioDeviceInfo> enabled, AudioDeviceInfo? target)
    {
        var candidates = enabled.ToArray();
        return candidates.FirstOrDefault(d =>
                   target is null || !string.Equals(d.Id, target.Id, StringComparison.OrdinalIgnoreCase))
               ?? candidates.FirstOrDefault();
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
