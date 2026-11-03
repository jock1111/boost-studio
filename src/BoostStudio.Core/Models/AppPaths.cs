namespace BoostStudio.Core.Models;

public static class AppPaths
{
    private const string FolderName = "BoostStudio";

    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string UserPresetsFile => Path.Combine(Root, "presets.json");

    public static string LogsDirectory => Path.Combine(Root, "logs");

    public static string LogFile => Path.Combine(LogsDirectory, "booststudio.log");

    public static string BackupsDirectory => Path.Combine(Root, "backups");

    public static string DiagnosticsDirectory => Path.Combine(Root, "diagnostics");

    public static string RuntimeDirectory => Path.Combine(Root, "runtime");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(BackupsDirectory);
        Directory.CreateDirectory(DiagnosticsDirectory);
        Directory.CreateDirectory(RuntimeDirectory);
    }
}

public sealed record AudioDeviceInfo(
    string Id,
    string Name,
    string DeviceName,
    string? InterfaceName,
    bool IsDefault,
    int SampleRate,
    int Channels,
    string State,
    string ScopePattern,
    bool ApoAssociated = false)
{
    public string DisplayName => IsDefault ? $"{Name}  •  Windows default" : Name;

    public string FormatSummary => SampleRate > 0
        ? $"{SampleRate / 1000.0:0.#} kHz · {Channels} ch"
        : "Format unavailable";

    /// <summary>
    /// True only when Equalizer APO's APO CLSID is registered on this endpoint.
    /// This proves the APO is associated with the device, not that audio was measured.
    /// </summary>
    public string ApoStatusText => ApoAssociated
        ? "Equalizer APO enabled"
        : "Equalizer APO not enabled";
}

public enum EngineState
{
    NotInstalled,
    NotIntegrated,
    Ready,
    Bypassed,
    PermissionDenied,
    ApoNotEnabled,
    Error,
    Detecting
}

public sealed record EngineStatus(
    EngineState State,
    bool Installed,
    string? InstallDirectory,
    string? ConfigDirectory,
    string? Version,
    bool ConfigWritable,
    bool IntegrationActive,
    bool ManagedFilePresent,
    double PreampDb,
    double HeadroomReservedDb,
    int ActiveFilterCount,
    DateTime? LastAppliedUtc,
    string Summary,
    string? Detail = null,
    int ApoEnabledDeviceCount = 0,
    bool ApoStatusChecked = false,
    bool TargetApoEnabled = false,
    string? ProcessingScope = null,
    string? VerificationText = null)
{
    public static EngineStatus Detecting { get; } = new(
        EngineState.Detecting, false, null, null, null, false, false, false,
        0, 0, 0, null, "Detecting audio engine…");

    public bool IsReady => State is EngineState.Ready or EngineState.Bypassed;

    /// <summary>Configuration is written, but the APO is not enabled on the target output.</summary>
    public bool NeedsApoSetup => State == EngineState.ApoNotEnabled;

    public string StateLabel => State switch
    {
        EngineState.NotInstalled => "Engine missing",
        EngineState.NotIntegrated => "Ready to connect",
        EngineState.Ready => "Configured",
        EngineState.Bypassed => "Bypassed",
        EngineState.PermissionDenied => "Permission required",
        EngineState.ApoNotEnabled => "APO not enabled",
        EngineState.Error => "Attention needed",
        EngineState.Detecting => "Detecting",
        _ => "Unknown"
    };
}

public sealed record EngineApplyResult(
    bool Success,
    bool Changed,
    EngineStatus Status,
    string? Error = null,
    string? ConfigPreview = null);

public sealed record EqualizerApoInstallation(
    string InstallDirectory,
    string ConfigDirectory,
    string ConfigTxtPath,
    string ManagedConfigPath,
    string? Version,
    string DetectionSource)
{
    public string? EditorPath
    {
        get
        {
            var candidates = new[]
            {
                Path.Combine(InstallDirectory, "Editor.exe"),
                Path.Combine(InstallDirectory, "ConfigurationEditor.exe"),
                Path.Combine(InstallDirectory, "config", "Editor.exe")
            };
            return candidates.FirstOrDefault(File.Exists);
        }
    }

    public string? DeviceSelectorPath
    {
        get
        {
            var candidates = new[]
            {
                Path.Combine(InstallDirectory, "DeviceSelector.exe"),
                Path.Combine(InstallDirectory, "Configurator.exe")
            };
            return candidates.FirstOrDefault(File.Exists);
        }
    }

    public string? EqualizerApoDllPath
    {
        get
        {
            var candidates = new[]
            {
                Path.Combine(InstallDirectory, "EqualizerAPO.dll"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "EqualizerAPO.dll")
            };
            return candidates.FirstOrDefault(File.Exists);
        }
    }
}

public sealed record SpectrumFrame(
    float[] Spectrum,
    float[] BassSpectrum,
    double RmsDb,
    double PeakDb,
    double PeakHoldDb,
    long ClippingSamples,
    bool IsClipping,
    int SampleRate,
    int Channels,
    double DominantBassHz,
    DateTime Timestamp);

public sealed record DeviceSwitchResult(bool Success, string Message);
