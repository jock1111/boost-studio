using Microsoft.Win32;

namespace BoostStudio.Core.Engine;

public enum ApoAssociation
{
    /// <summary>The endpoint's APO configuration could not be read.</summary>
    Unknown,

    /// <summary>The endpoint is readable but Equalizer APO is not attached to it.</summary>
    NotAssociated,

    /// <summary>Equalizer APO's APO CLSID is registered on the endpoint.</summary>
    Associated
}

/// <summary>
/// Reads Windows' per-endpoint APO registration to answer a question the Equalizer APO
/// configuration file cannot answer: is the APO actually attached to this output device?
/// This is evidence of association only, never proof of audible processing.
/// </summary>
public static class EqualizerApoApoDetector
{
    // Equalizer APO 1.4.x registers these COM classes from EqualizerAPO.dll. They are
    // stable across recent releases; the DLL path is verified separately by the locator.
    public static readonly string[] KnownApoClsids =
    {
        "{EACD2258-FCAC-4FF4-B36D-419E924A6D79}",
        "{EC1CC9CE-FAED-4822-828A-82A81A6F018F}"
    };

    private const string FxPropertyPrefix = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},";
    private const string RenderRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";

    /// <summary>Converts an MMDevice ID such as "{0.0.0.00000000}.{guid}" to its registry key.</summary>
    public static string? EndpointKeyFromDeviceId(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return null;

        var trimmed = deviceId.Trim();
        var separator = trimmed.LastIndexOf('.');
        var key = separator >= 0 ? trimmed[(separator + 1)..] : trimmed;
        return key.StartsWith('{') && key.EndsWith('}') ? key : null;
    }

    /// <summary>True when a FxProperties value references one of Equalizer APO's APO CLSIDs.</summary>
    public static bool ReferencesApo(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        foreach (var clsid in KnownApoClsids)
        {
            if (value.Contains(clsid, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static ApoAssociation Check(string? deviceId)
    {
        var endpoint = EndpointKeyFromDeviceId(deviceId);
        if (endpoint is null)
            return ApoAssociation.Unknown;

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var endpointKey = baseKey.OpenSubKey($@"{RenderRoot}\{endpoint}");
                if (endpointKey is null)
                    continue;

                using var fx = endpointKey.OpenSubKey("FxProperties");
                if (fx is null)
                    return ApoAssociation.NotAssociated;

                foreach (var name in fx.GetValueNames())
                {
                    if (!name.StartsWith(FxPropertyPrefix, StringComparison.OrdinalIgnoreCase) &&
                        !ReferencesApo(name))
                        continue;

                    if (ValueMatches(fx.GetValue(name)))
                        return ApoAssociation.Associated;
                }

                return ApoAssociation.NotAssociated;
            }
            catch
            {
                // Try the other registry view, then report unknown.
            }
        }

        return ApoAssociation.Unknown;
    }

    private static bool ValueMatches(object? value) => value switch
    {
        string text => ReferencesApo(text),
        string[] values => values.Any(ReferencesApo),
        _ => ReferencesApo(value?.ToString())
    };
}
