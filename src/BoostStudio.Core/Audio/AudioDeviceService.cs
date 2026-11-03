using System.Runtime.InteropServices;
using BoostStudio.Core.Engine;
using BoostStudio.Core.Models;
using BoostStudio.Core.Storage;
using NAudio.CoreAudioApi;

namespace BoostStudio.Core.Audio;

/// <summary>Windows Core Audio device discovery, using NAudio's WASAPI wrappers.</summary>
public sealed class AudioDeviceService
{
    private readonly ILogService? _log;

    public AudioDeviceService(ILogService? log = null)
    {
        _log = log;
    }

    public IReadOnlyList<AudioDeviceInfo> GetRenderDevices()
    {
        var result = new List<AudioDeviceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var defaultId = GetDefaultDeviceId();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var device in devices)
            {
                try
                {
                    result.Add(Describe(device, string.Equals(device.ID, defaultId, StringComparison.OrdinalIgnoreCase)));
                }
                finally
                {
                    device.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _log?.Error("Could not enumerate Windows render devices.", ex);
        }

        return result
            .OrderByDescending(device => device.IsDefault)
            .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public string? GetDefaultDeviceId()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.ID;
        }
        catch (Exception ex)
        {
            _log?.Warn($"No default render device available: {ex.Message}");
            return null;
        }
    }

    public string? GetDefaultDeviceName()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.FriendlyName;
        }
        catch
        {
            return null;
        }
    }

    public AudioDeviceInfo? GetDevice(string? deviceId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = string.IsNullOrWhiteSpace(deviceId)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : enumerator.GetDevice(deviceId);
            return Describe(device, device.ID == GetDefaultDeviceId());
        }
        catch (Exception ex)
        {
            _log?.Warn($"Could not open audio device '{deviceId ?? "default"}': {ex.Message}");
            return null;
        }
    }

    public MMDevice OpenDevice(string? deviceId)
    {
        var enumerator = new MMDeviceEnumerator();
        try
        {
            return string.IsNullOrWhiteSpace(deviceId)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : enumerator.GetDevice(deviceId);
        }
        finally
        {
            enumerator.Dispose();
        }
    }

    /// <summary>
    /// Sets the Windows default output device. Windows exposes no supported public API
    /// for this; the well-known audio policy COM interface is used as a best-effort
    /// convenience. Failure falls back to the Windows Settings guidance in the UI.
    /// </summary>
    public DeviceSwitchResult TrySetDefaultDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return new DeviceSwitchResult(false, "No device was selected.");

        object? comObject = null;
        try
        {
            var policy = (IPolicyConfig)(comObject = new CPolicyConfigClient());
            var results = new List<HRESULT>
            {
                (HRESULT)policy.SetDefaultEndpoint(deviceId, ERole.eConsole),
                (HRESULT)policy.SetDefaultEndpoint(deviceId, ERole.eMultimedia),
                (HRESULT)policy.SetDefaultEndpoint(deviceId, ERole.eCommunications)
            };

            if (results.All(hr => hr == HRESULT.S_OK))
                return new DeviceSwitchResult(true, "Windows default output changed.");

            var failed = results.First(hr => hr != HRESULT.S_OK);
            _log?.Warn($"SetDefaultEndpoint returned 0x{failed:X8}.");
            return new DeviceSwitchResult(false,
                "Windows rejected the device switch. Use Windows Settings → System → Sound.");
        }
        catch (Exception ex)
        {
            _log?.Error("Default output switching failed.", ex);
            return new DeviceSwitchResult(false,
                "This Windows build did not accept the device switch. Use Windows Settings → System → Sound.");
        }
        finally
        {
            if (comObject is not null && Marshal.IsComObject(comObject))
            {
                try { Marshal.FinalReleaseComObject(comObject); }
                catch { /* ignore */ }
            }
        }
    }

    private static AudioDeviceInfo Describe(MMDevice device, bool isDefault)
    {
        var name = SafeRead(() => device.FriendlyName, "Audio output");
        var deviceName = SafeRead(() => device.DeviceFriendlyName, name);
        string? interfaceName = null;
        var sampleRate = 0;
        var channels = 0;

        try
        {
            using var client = device.AudioClient;
            var format = client.MixFormat;
            sampleRate = format.SampleRate;
            channels = format.Channels;
        }
        catch
        {
            // Format is cosmetic; device can still be monitored.
        }

        var state = SafeRead(() => device.State.ToString(), "Unknown");
        var pattern = BuildScopePattern(deviceName, name, interfaceName);
        var apo = EqualizerApoApoDetector.Check(device.ID);

        return new AudioDeviceInfo(device.ID, name, deviceName, interfaceName, isDefault,
            sampleRate, channels, state, pattern, apo == ApoAssociation.Associated);
    }

    private static T SafeRead<T>(Func<T> getter, T fallback)
    {
        try { return getter(); }
        catch { return fallback; }
    }

    /// <summary>
    /// Equalizer APO's Device command matches every word in "device name + connection name + GUID".
    /// We generate a pattern from the endpoint and underlying device names; users can switch to
    /// "all devices" (the safe default) if a driver reports unusual names.
    /// </summary>
    public static string BuildScopePattern(string deviceName, string friendlyName, string? interfaceName)
    {
        var connection = friendlyName;
        var parenthesis = friendlyName.IndexOf(" (", StringComparison.Ordinal);
        if (parenthesis > 0)
            connection = friendlyName[..parenthesis];

        var source = $"{deviceName} {interfaceName} {connection}";
        var tokens = new List<string>();
        var current = new List<char>();

        void Flush()
        {
            if (current.Count >= 2)
            {
                var token = new string(current.ToArray());
                if (!tokens.Contains(token, StringComparer.OrdinalIgnoreCase))
                    tokens.Add(token);
            }

            current.Clear();
        }

        foreach (var character in source)
        {
            if (char.IsLetterOrDigit(character) || character is '+' or '#')
                current.Add(character);
            else
                Flush();
        }

        Flush();

        // Do not require one-character tokens such as the "R" from "Realtek(R)".
        var pattern = string.Join(' ', tokens);
        return string.IsNullOrWhiteSpace(pattern) ? SanitizeFallback(deviceName) : pattern;
    }

    private static string SanitizeFallback(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) || c == ' ' ? c : ' ').ToArray();
        var result = string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(result) ? "all" : result;
    }

    private enum ERole
    {
        eConsole = 0,
        eMultimedia = 1,
        eCommunications = 2
    }

    private enum HRESULT
    {
        S_OK = 0
    }

    [ComImport]
    [Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class CPolicyConfigClient
    {
    }

    [ComImport]
    [Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, out IntPtr ppFormat);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, [MarshalAs(UnmanagedType.Bool)] bool bDefault, out IntPtr ppFormat);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pEndpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, [MarshalAs(UnmanagedType.Bool)] bool bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pmftPeriod);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pMode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, [MarshalAs(UnmanagedType.Bool)] bool bFxStore, IntPtr key, IntPtr pv);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, [MarshalAs(UnmanagedType.Bool)] bool bFxStore, IntPtr key, IntPtr pv);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, ERole role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, [MarshalAs(UnmanagedType.Bool)] bool bVisible);
    }
}
