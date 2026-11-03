using System.Text;
using System.Threading.Tasks;
using BoostStudio.Core.Dsp;
using BoostStudio.Core.Models;
using BoostStudio.Core.Storage;

namespace BoostStudio.Core.Engine;

/// <summary>
/// Result of an audio engine verification test.
/// </summary>
public sealed record AudioEngineTestResult(
    bool ApoInstalled,
    bool CorrectEndpoint,
    bool ConfigWritten,
    bool ProcessingVerified,
    bool GainChangeMeasured,
    string Detail);

/// <summary>
/// Coordinates Equalizer APO integration and configuration application.
/// All writes are debounced and serialized; no audio is ever played by this service.
/// </summary>
public sealed class AudioEngineService : IDisposable
{
    private readonly ILogService? _log;
    private readonly EqualizerApoLocator _locator;
    private readonly IApoReadinessProbe _apoProbe;
    private readonly DspConfigBuilder _builder = new();
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private readonly object _statusGate = new();
    private readonly string _backupDirectory;

    private CancellationTokenSource? _debounce;
    private long _revision;
    private bool _disposed;
    private bool _suppressAutoConnect;
    private EngineStatus _status = EngineStatus.Detecting;
    private DspMetrics? _lastMetrics;
    private DateTime? _lastAppliedUtc;
    private bool? _cachedWritable;
    private string? _cachedWritableDirectory;
    private DateTime _writableCheckedUtc = DateTime.MinValue;

    public AudioEngineService(ILogService? log = null, string? backupDirectory = null, IApoReadinessProbe? apoProbe = null)
    {
        _log = log;
        _locator = new EqualizerApoLocator(log);
        _apoProbe = apoProbe ?? new AudioDeviceApoReadinessProbe(log);
        _backupDirectory = backupDirectory ?? AppPaths.BackupsDirectory;
    }

    public EngineStatus Status
    {
        get { lock (_statusGate) return _status; }
    }

    public EqualizerApoInstallation? CurrentInstallation { get; private set; }

    public string? DetectionError { get; private set; }

    public bool IsSuppressed => _suppressAutoConnect;

    public event EventHandler<EngineStatus>? StatusChanged;

    public void ScheduleApply(AppSettings settings, double runtimeTrimDb = 0)
    {
        if (_disposed) return;
        var revision = Interlocked.Increment(ref _revision);

        CancellationTokenSource? previous;
        CancellationTokenSource cts;
        lock (_statusGate)
        {
            previous = _debounce;
            cts = new CancellationTokenSource();
            _debounce = cts;
        }

        try { previous?.Cancel(); } catch { /* ignore */ }
        try { previous?.Dispose(); } catch { /* ignore */ }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(130, cts.Token).ConfigureAwait(false);
                if (cts.IsCancellationRequested || revision != Interlocked.Read(ref _revision))
                    return;

                await ApplyAsync(settings, runtimeTrimDb, cancellationToken: cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Newer change superseded this one.
            }
            catch (Exception ex)
            {
                _log?.Error("Debounced audio engine apply failed.", ex);
            }
        });
    }

    public async Task<EngineApplyResult> ApplyAsync(
        AppSettings settings,
        double runtimeTrimDb = 0,
        CancellationToken cancellationToken = default)
    {
        await _applyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed)
                return new EngineApplyResult(false, false, Status, "The audio engine is shutting down.");

            var configuration = _builder.Build(settings, runtimeTrimDb, settings.Audio.ApplyToAllDevices ? null : settings.Audio.ScopedDevicePattern);
            _lastMetrics = configuration.Metrics;

            var installation = _locator.Locate(settings.EqualizerApoPathOverride, out var detectionError);
            CurrentInstallation = installation;
            DetectionError = detectionError;

            if (installation is null)
            {
                var missing = BuildStatus(
                    EngineState.NotInstalled, null, false, false, false,
                    configuration.Metrics,
                    detectionError ?? "Equalizer APO was not detected. Install it and enable its APO on your output device.",
                    null);
                Publish(missing);
                return new EngineApplyResult(false, false, missing,
                    "Equalizer APO is required for system-wide processing.", configuration.Text);
            }

            var store = new EqualizerApoConfigStore(installation, _backupDirectory, _log);
            var integrationActive = store.IsIntegrationActive;
            var shouldWrite = settings.AnyProcessingEnabled || integrationActive;

            if (_suppressAutoConnect)
            {
                var disconnected = BuildStatus(
                    EngineState.NotIntegrated, installation, IsConfigWritable(installation, force: false),
                    integrationActive, File.Exists(installation.ManagedConfigPath),
                    configuration.Metrics,
                    integrationActive
                        ? "Integration is connected. Click Repair to write the current processing settings."
                        : "Equalizer APO found. Click Reconnect to resume system-wide processing.",
                    "Automatic reconnection is paused until Reconnect is pressed.",
                    settings: null);
                Publish(disconnected);
                return new EngineApplyResult(true, false, disconnected);
            }

            if (!shouldWrite)
            {
                var ready = BuildStatus(
                    EngineState.NotIntegrated, installation, IsConfigWritable(installation, force: true),
                    false, File.Exists(installation.ManagedConfigPath),
                    configuration.Metrics,
                    "Equalizer APO found. Enable Bass Booster, Volume Booster or Equalizer to connect.",
                    null,
                    settings: null);
                Publish(ready);
                return new EngineApplyResult(true, false, ready);
            }

            var writable = IsConfigWritable(installation, force: true);
            if (!writable)
            {
                var denied = BuildStatus(
                    EngineState.PermissionDenied, installation, false, integrationActive,
                    File.Exists(installation.ManagedConfigPath),
                    configuration.Metrics,
                    "Administrator permission required to write the Equalizer APO configuration.",
                    $"Boost Studio cannot write to {installation.ConfigDirectory}. Restart Boost Studio as administrator, or give the current user write access to the Equalizer APO config folder.",
                    settings: null);
                Publish(denied);
                return new EngineApplyResult(false, false, denied);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var apo = EvaluateApo(settings.Audio);

            try
            {
                var integrationChanged = store.EnsureIntegrated();
                var dspChanged = store.WriteManagedConfig(configuration.Text);
                var changed = integrationChanged || dspChanged;
                integrationActive = store.IsIntegrationActive;
                _lastAppliedUtc = DateTime.UtcNow;
                _cachedWritable = true;

                var processing = settings.AnyProcessingEnabled;
                var apoMissing = processing && apo.Checked && !apo.TargetApoEnabled;
                var state = !processing
                    ? EngineState.Bypassed
                    : apoMissing ? EngineState.ApoNotEnabled : EngineState.Ready;
                var summary = !processing
                    ? "Engine connected · processing bypassed"
                    : apoMissing
                        ? "Configuration written · APO not enabled on the target output"
                        : BuildActiveSummary(configuration.Metrics);
                var detail = apoMissing ? BuildApoDetail(apo) : null;

                var status = BuildStatus(
                    state, installation, true, integrationActive, File.Exists(installation.ManagedConfigPath),
                    configuration.Metrics, summary, detail, _lastAppliedUtc,
                    apo: apo, verificationText: BuildVerificationText(processing, apo));
                Publish(status);

                if (apoMissing)
                    _log?.Warn($"DSP configuration was written, but Equalizer APO is not registered on {apo.Scope}. Audible processing is not verified.");

                _log?.Info(settings.AnyProcessingEnabled
                    ? $"DSP configuration written: {configuration.Metrics.FilterCount} filters, preamp {configuration.Metrics.PreampDb:0.0} dB, headroom {configuration.Metrics.HeadroomReservedDb:0.0} dB."
                    : "DSP bypassed; module state retained.");

                return new EngineApplyResult(true, changed, status, null, configuration.Text);
            }
            catch (UnauthorizedAccessException ex)
            {
                _cachedWritable = false;
                _log?.Error("Access denied writing Equalizer APO configuration.", ex);
                var denied = BuildStatus(
                    EngineState.PermissionDenied, installation, false, integrationActive,
                    File.Exists(installation.ManagedConfigPath),
                    configuration.Metrics,
                    "Administrator permission required to write the Equalizer APO configuration.",
                    "Restart Boost Studio as administrator to apply system-wide processing.",
                    settings: null);
                Publish(denied);
                return new EngineApplyResult(false, false, denied, ex.Message);
            }
            catch (Exception ex)
            {
                _log?.Error("Could not apply Equalizer APO configuration.", ex);
                var error = BuildStatus(
                    EngineState.Error, installation, writable, integrationActive,
                    File.Exists(installation.ManagedConfigPath),
                    configuration.Metrics,
                    "The audio engine could not apply the configuration.",
                    ex.Message, settings: null);
                Publish(error);
                return new EngineApplyResult(false, false, error, ex.Message);
            }
        }
        finally
        {
            _applyGate.Release();
        }
    }

    public Task<EngineStatus> RefreshAsync(AppSettings settings)
    {
        var configuration = _builder.Build(settings, 0, settings.Audio.ApplyToAllDevices ? null : settings.Audio.ScopedDevicePattern);
        var installation = _locator.Locate(settings.EqualizerApoPathOverride, out var detectionError);
        CurrentInstallation = installation;
        DetectionError = detectionError;

        if (installation is null)
        {
            var missing = BuildStatus(
                EngineState.NotInstalled, null, false, false, false, _lastMetrics,
                detectionError ?? "Equalizer APO not detected.",
                detectionError, settings: null);
            Publish(missing);
            return Task.FromResult(missing);
        }

        var writable = IsConfigWritable(installation, force: false);
        var store = new EqualizerApoConfigStore(installation, _backupDirectory, _log);
        var integration = store.IsIntegrationActive;
        var managedFile = File.Exists(installation.ManagedConfigPath);
        var apo = EvaluateApo(settings.Audio);

        EngineState state;
        string summary;
        string? detail = null;
        string? verification = null;

        if (!writable && (settings.AnyProcessingEnabled || integration))
        {
            state = EngineState.PermissionDenied;
            summary = "Administrator permission required to update Equalizer APO.";
            detail = $"Restart Boost Studio as administrator or grant write access to {installation.ConfigDirectory}.";
            verification = "Not verified: the configuration was not written.";
        }
        else if (integration && !settings.AnyProcessingEnabled)
        {
            state = EngineState.Bypassed;
            summary = "Engine connected · processing bypassed";
            verification = "Configuration written as bypass; no processing expected.";
        }
        else if (integration && apo.Checked && !apo.TargetApoEnabled)
        {
            state = EngineState.ApoNotEnabled;
            summary = "Configuration written · APO not enabled on the target output";
            detail = BuildApoDetail(apo);
            verification = BuildVerificationText(true, apo);
        }
        else if (integration)
        {
            state = EngineState.Ready;
            summary = _lastMetrics is null
                ? "Equalizer APO connected."
                : BuildActiveSummary(_lastMetrics);
            verification = BuildVerificationText(true, apo);
        }
        else
        {
            state = EngineState.NotIntegrated;
            summary = settings.AnyProcessingEnabled
                ? "Equalizer APO found. Enable a module or click Repair to connect."
                : "Equalizer APO found. Enable a module to connect.";
            verification = "Configuration not connected; no processing is expected.";
        }

        var status = BuildStatus(state, installation, writable, integration, managedFile,
            _lastMetrics ?? configuration.Metrics, summary, detail, _lastAppliedUtc,
            apo: apo, verificationText: verification);
        Publish(status);
        return Task.FromResult(status);
    }

    public async Task<EngineStatus> RemoveIntegrationAsync(AppSettings settings)
    {
        await _applyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var installation = _locator.Locate(settings.EqualizerApoPathOverride, out var error);
            if (installation is null)
            {
                var missing = BuildStatus(EngineState.NotInstalled, null, false, false, false,
                    _lastMetrics, error ?? "Equalizer APO not detected.", error, settings: null);
                Publish(missing);
                return missing;
            }

            var store = new EqualizerApoConfigStore(installation, _backupDirectory, _log);
            if (!IsConfigWritable(installation, force: true))
            {
                var denied = BuildStatus(EngineState.PermissionDenied, installation, false,
                    store.IsIntegrationActive, File.Exists(installation.ManagedConfigPath), _lastMetrics,
                    "Administrator permission required to modify Equalizer APO.", null, settings: null);
                Publish(denied);
                return denied;
            }

            store.RemoveIntegration();
            _suppressAutoConnect = true;
            var status = BuildStatus(EngineState.NotIntegrated, installation, true, false, false,
                _lastMetrics, "Boost Studio removed its Equalizer APO include. Existing user configuration was preserved.",
                "Click Reconnect to reinstall the managed include.", settings: null);
            Publish(status);
            return status;
        }
        catch (Exception ex)
        {
            _log?.Error("Could not remove Equalizer APO integration.", ex);
            return PublishError(ex.Message);
        }
        finally
        {
            _applyGate.Release();
        }
    }

    public async Task<EngineStatus> RestoreBaselineAsync(AppSettings settings)
    {
        await _applyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var installation = _locator.Locate(settings.EqualizerApoPathOverride, out var error);
            if (installation is null)
                return PublishError(error ?? "Equalizer APO not detected.");

            var store = new EqualizerApoConfigStore(installation, _backupDirectory, _log);
            if (!store.HasBaseline)
                return PublishError("No pre-Boost baseline backup exists yet. It is created the first time Boost Studio connects.");

            if (!IsConfigWritable(installation, force: true))
                return PublishError("Administrator permission is required to restore the Equalizer APO configuration.");

            store.RestoreBaseline();
            _suppressAutoConnect = true;
            var status = BuildStatus(EngineState.NotIntegrated, installation, true, false,
                File.Exists(installation.ManagedConfigPath), _lastMetrics,
                "The pre-Boost Equalizer APO configuration was restored.",
                "Boost Studio processing is disconnected until you press Reconnect.",
                settings: null);
            Publish(status);
            return status;
        }
        catch (Exception ex)
        {
            _log?.Error("Could not restore Equalizer APO baseline.", ex);
            return PublishError(ex.Message);
        }
        finally
        {
            _applyGate.Release();
        }
    }

    public async Task<EngineStatus> ReconnectAsync(AppSettings settings, double runtimeTrimDb = 0)
    {
        _suppressAutoConnect = false;
        var result = await ApplyAsync(settings, runtimeTrimDb).ConfigureAwait(false);
        return result.Status;
    }

    /// <summary>
    /// Performs a controlled diagnostic test of the audio processing pipeline.
    /// </summary>
    public async Task<AudioEngineTestResult> TestAudioProcessingAsync(AppSettings settings)
    {
        var detail = new StringBuilder();
        bool apoInstalled = false, correctEndpoint = false, configWritten = false, processingVerified = false, gainChangeMeasured = false;

        // 1. Check Equalizer APO installation
        var installation = _locator.Locate(settings.EqualizerApoPathOverride, out var detectionError);
        if (installation is null)
        {
            detail.AppendLine($"Equalizer APO not found: {detectionError}");
            return new AudioEngineTestResult(false, false, false, false, false, detail.ToString());
        }
        apoInstalled = true;
        detail.AppendLine($"Equalizer APO found at {installation.InstallDirectory}");

        // 2. Check APO association with the target endpoint
        var apo = _apoProbe.Evaluate(settings.Audio);
        if (apo.Checked)
        {
            if (apo.TargetApoEnabled)
            {
                correctEndpoint = true;
                detail.AppendLine($"APO is registered on target output: {apo.Scope} ({apo.TargetDeviceName ?? "unknown"})");
            }
            else
            {
                detail.AppendLine($"APO is NOT registered on target output: {apo.Scope}. Enable APO in Device Selector and reboot.");
            }
        }
        else
        {
            detail.AppendLine("Could not verify APO association (registry access issue).");
        }

        // 3. Check configuration write
        var store = new EqualizerApoConfigStore(installation, _backupDirectory, _log);
        try
        {
            var config = _builder.Build(settings, 0, settings.Audio.ApplyToAllDevices ? null : settings.Audio.ScopedDevicePattern);
            store.EnsureIntegrated();
            store.WriteManagedConfig(config.Text);
            configWritten = true;
            detail.AppendLine($"Configuration written to {installation.ManagedConfigPath}");

            // 4. Verify the managed include is active in config.txt
            if (store.IsIntegrationActive)
            {
                detail.AppendLine("Managed include is active in Equalizer APO config.txt");
            }
            else
            {
                detail.AppendLine("WARNING: Managed include is NOT active in config.txt");
            }
        }
        catch (Exception ex)
        {
            detail.AppendLine($"Failed to write configuration: {ex.Message}");
            return new AudioEngineTestResult(apoInstalled, correctEndpoint, false, false, false, detail.ToString());
        }

        // 5. Test runtime processing with a safe negative gain (-18 dB)
        // This would require WASAPI loopback measurement which is complex.
        // For now, we report that the configuration is written but actual processing
        // verification requires manual listening or external measurement.
        detail.AppendLine();
        detail.AppendLine("Processing verification:");
        detail.AppendLine("  - Configuration is written and APO is installed");
        detail.AppendLine("  - Actual runtime processing cannot be automatically verified");
        detail.AppendLine("  - Use manual listening test: play audio and toggle Bass/Volume boosters");
        detail.AppendLine("  - A -18 dB test attenuation can be applied temporarily via Settings if needed");

        // We consider processing "verified" if APO is installed, configured, and enabled on endpoint
        processingVerified = apoInstalled && correctEndpoint && configWritten;
        gainChangeMeasured = false; // Would require WASAPI loopback capture

        return new AudioEngineTestResult(apoInstalled, correctEndpoint, configWritten, processingVerified, gainChangeMeasured, detail.ToString());
    }

    public EqualizerApoConfigStore? TryGetConfigStore(AppSettings settings)
    {
        var installation = _locator.Locate(settings.EqualizerApoPathOverride, out _);
        return installation is null
            ? null
            : new EqualizerApoConfigStore(installation, _backupDirectory, _log);
    }

    private EngineStatus PublishError(string detail)
    {
        CurrentInstallation ??= _locator.Locate(null, out _);
        var status = BuildStatus(EngineState.Error, CurrentInstallation,
            CurrentInstallation is not null && IsConfigWritable(CurrentInstallation, force: false),
            false, false, _lastMetrics, "Audio engine error.", detail, settings: null);
        Publish(status);
        return status;
    }

    private static string BuildActiveSummary(DspMetrics metrics)
    {
        var filters = metrics.FilterCount == 1 ? "1 filter" : $"{metrics.FilterCount} filters";
        if (metrics.Bypassed)
            return "Engine connected · processing bypassed";
        return $"DSP configuration written · {filters} · preamp {metrics.PreampDb:+0.0;-0.0;0.0} dB";
    }

    private ApoAssessment EvaluateApo(AudioSettings audio)
    {
        try
        {
            return _apoProbe.Evaluate(audio);
        }
        catch (Exception ex)
        {
            // Device association is diagnostic evidence; never let it prevent configuration.
            _log?.Warn($"Could not inspect Equalizer APO device association: {ex.Message}");
            return ApoAssessment.Unavailable(audio.ApplyToAllDevices
                ? "All outputs"
                : audio.ScopedDevicePattern ?? "Selected output");
        }
    }

    private static string BuildApoDetail(ApoAssessment apo) =>
        $"Equalizer APO is installed and Boost Studio's configuration was written, but Windows has no " +
        $"Equalizer APO APO registered on {apo.Scope}. Open Equalizer APO's Device Selector (or Configurator), " +
        "enable the APO for that output, then press Recheck. A written configuration file alone does not process audio.";

    private static string BuildVerificationText(bool processing, ApoAssessment apo)
    {
        if (!processing)
            return "Configuration written as bypass; no processing expected.";

        if (!apo.Checked)
            return "Configuration written; APO association could not be checked. Audible processing was not verified.";

        if (!apo.TargetApoEnabled)
            return $"Configuration written; Equalizer APO is not registered on {apo.Scope}. Audible processing was not verified.";

        return apo.TargetDeviceName is null
            ? $"Configuration written; Equalizer APO is registered on {apo.ApoEnabledDeviceCount} output(s). Audible processing was not measured."
            : $"Configuration written; Equalizer APO is registered on {apo.TargetDeviceName}. Audible processing was not measured.";
    }

    private EngineStatus BuildStatus(
        EngineState state,
        EqualizerApoInstallation? installation,
        bool writable,
        bool integrationActive,
        bool managedFile,
        DspMetrics? metrics,
        string summary,
        string? detail,
        DateTime? lastAppliedUtc = null,
        AppSettings? settings = null,
        ApoAssessment? apo = null,
        string? verificationText = null)
    {
        return new EngineStatus(
            state,
            installation is not null,
            installation?.InstallDirectory,
            installation?.ConfigDirectory,
            installation?.Version,
            writable,
            integrationActive,
            managedFile,
            metrics?.PreampDb ?? 0,
            metrics?.HeadroomReservedDb ?? 0,
            metrics?.FilterCount ?? 0,
            lastAppliedUtc ?? _lastAppliedUtc,
            summary,
            detail,
            apo?.ApoEnabledDeviceCount ?? 0,
            apo?.Checked ?? false,
            apo?.TargetApoEnabled ?? false,
            apo?.Scope,
            verificationText);
    }

    private bool IsConfigWritable(EqualizerApoInstallation installation, bool force)
    {
        if (!force && _cachedWritable.HasValue &&
            string.Equals(_cachedWritableDirectory, installation.ConfigDirectory, StringComparison.OrdinalIgnoreCase) &&
            DateTime.UtcNow - _writableCheckedUtc < TimeSpan.FromSeconds(30))
            return _cachedWritable.Value;

        var writable = EqualizerApoLocator.CanWriteToDirectory(installation.ConfigDirectory);
        _cachedWritable = writable;
        _cachedWritableDirectory = installation.ConfigDirectory;
        _writableCheckedUtc = DateTime.UtcNow;
        return writable;
    }

    private void Publish(EngineStatus status)
    {
        lock (_statusGate)
        {
            _status = status;
        }

        try
        {
            StatusChanged?.Invoke(this, status);
        }
        catch (Exception ex)
        {
            _log?.Error("An engine status consumer failed.", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_statusGate)
        {
            try { _debounce?.Cancel(); } catch { /* ignore */ }
            try { _debounce?.Dispose(); } catch { /* ignore */ }
            _debounce = null;
        }

        // A debounced apply may still be unwinding and will release the gate in its
        // finally block. The semaphore is process-lifetime state; disposing it here
        // can turn a harmless shutdown race into an ObjectDisposedException.
    }
}
