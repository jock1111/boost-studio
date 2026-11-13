using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using BoostStudio.Core.Audio;
using BoostStudio.Core.Dsp;
using BoostStudio.Core.Models;
using BoostStudio.Core.Storage;

namespace BoostStudio.Core.Engine;

/// <summary>
/// Result of an audio engine verification test. Every boolean in this record is backed by
/// evidence gathered during the test; <see cref="ProcessingVerified"/> and
/// <see cref="GainChangeMeasured"/> are only true after a real loopback measurement.
/// </summary>
public sealed record AudioEngineTestResult(
    bool ApoInstalled,
    bool CorrectEndpoint,
    bool ConfigWritten,
    bool ProcessingVerified,
    bool GainChangeMeasured,
    string Detail,
    string? DeviceName = null,
    string? DeviceId = null,
    double? MeasuredDeltaDb = null,
    double ExpectedDeltaDb = -18.0,
    IReadOnlyList<AudioTestStepResult>? Steps = null,
    bool BassResponseMeasured = false,
    bool VolumeGainMeasured = false,
    bool BoostersIndependent = false,
    string? ReportPath = null);

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
    private readonly AudioProcessingVerifier _verifier;
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

    // Verification is valid only for the exact DSP configuration that was measured.
    // Comment-only changes (including the generated timestamp) do not invalidate it.
    private string? _verifiedConfigFingerprint;
    private DateTime? _lastVerifiedUtc;
    private double? _lastMeasuredDeltaDb;
    private volatile bool _verificationInProgress;

    public AudioEngineService(ILogService? log = null, string? backupDirectory = null, IApoReadinessProbe? apoProbe = null)
    {
        _log = log;
        _locator = new EqualizerApoLocator(log);
        _apoProbe = apoProbe ?? new AudioDeviceApoReadinessProbe(log);
        _verifier = new AudioProcessingVerifier(log);
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

        // Capture the state on the caller's thread. The debounce runs on a worker
        // thread while the user can keep moving knobs, which would otherwise let the
        // DSP builder observe a half-updated settings graph.
        var snapshot = settings.Clone();
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
        // Do not dispose the superseded source here: its debounce task may still be
        // reading cts.Token (for example while waiting on the apply gate), which
        // throws ObjectDisposedException. Cancellation alone is enough; the source
        // is collected once the task finishes.

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(130, cts.Token).ConfigureAwait(false);
                if (cts.IsCancellationRequested || revision != Interlocked.Read(ref _revision))
                    return;

                await ApplyAsync(snapshot, runtimeTrimDb, cancellationToken: cts.Token).ConfigureAwait(false);
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
        // Defensive snapshot: callers may pass the live settings object and this
        // method continues on a worker thread after the apply gate is acquired.
        settings = settings.Clone();
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

            TryRecoverInterruptedSelfTest(installation);

            var store = new EqualizerApoConfigStore(installation, _backupDirectory, _log);
            var integrationActive = store.IsIntegrationActive;
            var shouldWrite = settings.AnyProcessingEnabled || integrationActive;
            var configFingerprint = ComputeConfigFingerprint(configuration.Text);
            var processingVerified = settings.AnyProcessingEnabled &&
                                     _verifiedConfigFingerprint is not null &&
                                     string.Equals(_verifiedConfigFingerprint, configFingerprint, StringComparison.Ordinal);

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
                processingVerified &= !apoMissing;

                var status = BuildStatus(
                    state, installation, true, integrationActive, File.Exists(installation.ManagedConfigPath),
                    configuration.Metrics, summary, detail, _lastAppliedUtc,
                    apo: apo,
                    verificationText: BuildVerificationText(processing, apo, processingVerified,
                        _lastMeasuredDeltaDb, _lastVerifiedUtc),
                    processingVerified: processingVerified,
                    verifiedUtc: _lastVerifiedUtc,
                    measuredDeltaDb: _lastMeasuredDeltaDb);
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

        TryRecoverInterruptedSelfTest(installation);

        var writable = IsConfigWritable(installation, force: false);
        var store = new EqualizerApoConfigStore(installation, _backupDirectory, _log);
        var integration = store.IsIntegrationActive;
        var managedFile = File.Exists(installation.ManagedConfigPath);
        var apo = EvaluateApo(settings.Audio);
        var processingVerified = settings.AnyProcessingEnabled && integration &&
                                 _verifiedConfigFingerprint is not null &&
                                 string.Equals(_verifiedConfigFingerprint,
                                     ComputeConfigFingerprint(configuration.Text), StringComparison.Ordinal);

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
            processingVerified = false;
        }
        else if (integration && !settings.AnyProcessingEnabled)
        {
            state = EngineState.Bypassed;
            summary = "Engine connected · processing bypassed";
            verification = "Configuration written as bypass; no processing expected.";
            processingVerified = false;
        }
        else if (integration && apo.Checked && !apo.TargetApoEnabled)
        {
            state = EngineState.ApoNotEnabled;
            summary = "Configuration written · APO not enabled on the target output";
            detail = BuildApoDetail(apo);
            verification = BuildVerificationText(true, apo);
            processingVerified = false;
        }
        else if (integration)
        {
            state = EngineState.Ready;
            summary = _lastMetrics is null
                ? "Equalizer APO connected."
                : BuildActiveSummary(_lastMetrics);
            verification = BuildVerificationText(true, apo, processingVerified,
                _lastMeasuredDeltaDb, _lastVerifiedUtc);
        }
        else
        {
            state = EngineState.NotIntegrated;
            summary = settings.AnyProcessingEnabled
                ? "Equalizer APO found. Enable a module or click Repair to connect."
                : "Equalizer APO found. Enable a module to connect.";
            verification = "Configuration not connected; no processing is expected.";
            processingVerified = false;
        }

        var status = BuildStatus(state, installation, writable, integration, managedFile,
            _lastMetrics ?? configuration.Metrics, summary, detail, _lastAppliedUtc,
            apo: apo, verificationText: verification,
            processingVerified: processingVerified,
            verifiedUtc: _lastVerifiedUtc,
            measuredDeltaDb: _lastMeasuredDeltaDb);
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
    /// Runs a real, measured verification of the complete system-wide audio path:
    /// Equalizer APO include -> DSP configuration -> Windows shared-mode mixer -> WASAPI
    /// loopback. The -18 dB step proves that a configuration change reaches the audio
    /// engine. Bass and volume steps prove that the generated filter and preamp actually
    /// change the measured signal. The user's configuration is restored in a finally block.
    /// </summary>
    public async Task<AudioEngineTestResult> TestAudioProcessingAsync(AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        settings = settings.Clone();
        var detail = new StringBuilder();
        await _applyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        _verificationInProgress = true;
        try
        {
            var configuration = _builder.Build(settings, 0,
                settings.Audio.ApplyToAllDevices ? null : settings.Audio.ScopedDevicePattern);
            _lastMetrics = configuration.Metrics;

            // 1. Detect the engine.
            var installation = _locator.Locate(settings.EqualizerApoPathOverride, out var detectionError);
            CurrentInstallation = installation;
            DetectionError = detectionError;
            if (installation is null)
            {
                detail.AppendLine($"Equalizer APO not found: {detectionError}");
                return new AudioEngineTestResult(false, false, false, false, false,
                    detail.ToString());
            }

            detail.AppendLine($"Equalizer APO: {installation.Version ?? "version unknown"}");
            detail.AppendLine($"Install folder: {installation.InstallDirectory}");
            detail.AppendLine($"Config folder: {installation.ConfigDirectory}");
            detail.AppendLine($"Managed file: {installation.ManagedConfigPath}");

            // 2. Inspect APO association. This is evidence only; the measurement below
            //    decides whether audio is actually processed.
            var apo = EvaluateApo(settings.Audio);
            var correctEndpoint = apo.Checked && apo.TargetApoEnabled;
            detail.AppendLine(apo.Checked
                ? $"APO association: {(apo.TargetApoEnabled ? "registered" : "NOT registered")} on {apo.Scope}" +
                  (apo.TargetDeviceName is null ? string.Empty : $" ({apo.TargetDeviceName})")
                : "APO association: could not be read from the registry.");
            detail.AppendLine($"APO-enabled active outputs: {apo.ApoEnabledDeviceCount}");

            // 3. Prepare a writable, integrated configuration.
            if (!IsConfigWritable(installation, force: true))
            {
                detail.AppendLine();
                detail.AppendLine("The Equalizer APO config folder is not writable. Grant access or run as administrator, then retry.");
                return new AudioEngineTestResult(true, correctEndpoint, false, false, false,
                    detail.ToString());
            }

            var store = new EqualizerApoConfigStore(installation, _backupDirectory, _log);
            store.EnsureIntegrated();
            if (!store.IsIntegrationActive)
            {
                detail.AppendLine();
                detail.AppendLine("Boost Studio's managed include is not active in Equalizer APO's config.txt. Click Connect / repair, then retry.");
                return new AudioEngineTestResult(true, correctEndpoint, false, false, false,
                    detail.ToString());
            }

            var original = store.ReadManagedText();
            var restoreMarker = Path.Combine(AppPaths.RuntimeDirectory, "eapo-selftest-restore.txt");
            TryWriteRestoreMarker(restoreMarker, original);
            detail.AppendLine("Managed include: active");
            detail.AppendLine($"Original managed configuration saved ({original.Length} characters).");

            // 4. Build measured steps. The first pair uses the user's actual generated
            //    configuration, so a pass proves that this exact configuration reaches
            //    the audio engine. The remaining pairs isolate the bass shelf and the
            //    volume preamp.
            var actualPreamp = configuration.Metrics.PreampDb;
            var toneAmplitude = actualPreamp < -12 ? 0.16 : AudioProcessingVerifier.DefaultAmplitude;
            var attenuated = AddPreampOffset(configuration.Text, -18.0);
            var flat = BuildFlatTestConfig();
            var volumeSettings = BuildVolumeTestSettings(settings);
            var volumeConfig = _builder.Build(volumeSettings).Text;
            var volumeExpected = Math.Round(volumeSettings.Volume.EffectiveBoostDb + volumeSettings.Volume.PreampDb, 2);
            var bassSettings = BuildBassTestSettings(settings);
            var bassConfig = _builder.Build(bassSettings).Text;
            var bassFrequency = Math.Clamp(bassSettings.Bass.FrequencyHz, 30, 120);
            var bassFilter = DspMath.LowShelf(bassSettings.Bass.FrequencyHz,
                bassSettings.Bass.EffectiveGainDb, bassSettings.Bass.ShelfQ, DspMath.DefaultSampleRate);
            var bassExpected = Math.Round(bassFilter.GainDbAt(bassFrequency, DspMath.DefaultSampleRate), 2);
            var bassAtOneK = Math.Round(bassFilter.GainDbAt(1000, DspMath.DefaultSampleRate), 2);

            var steps = new List<AudioTestStep>
            {
                new("actual_1k", "Current DSP configuration (1 kHz baseline)", configuration.Text, 1000,
                    Amplitude: toneAmplitude),
                new("atten_1k", "Current DSP configuration with -18 dB test preamp", attenuated, 1000,
                    "actual_1k", -18.0, 2.0, toneAmplitude),
                new("flat_1k", "Flat reference (1 kHz)", flat, 1000),
                new("volume_1k", "Volume Booster preamp (1 kHz)", volumeConfig, 1000,
                    "flat_1k", volumeExpected, 1.8),
                new("flat_bass", "Flat reference (bass test frequency)", flat, bassFrequency),
                new("bass_freq", "Bass Booster low shelf (bass test frequency)", bassConfig, bassFrequency,
                    "flat_bass", bassExpected, 2.0),
                new("bass_1k", "Bass Booster frequency selectivity (1 kHz)", bassConfig, 1000,
                    "flat_1k", bassAtOneK, 1.8)
            };

            var deviceId = settings.Audio.ApplyToAllDevices ? null : settings.Audio.ScopedDeviceId;
            AudioTestReport report;
            try
            {
                report = await _verifier.RunAsync(deviceId, steps,
                        (text, token) =>
                        {
                            store.WriteManagedConfig(text);
                            return Task.CompletedTask;
                        }, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                RestoreManagedConfig(store, original, restoreMarker);
            }

            detail.AppendLine();
            detail.AppendLine($"Loopback device: {report.DeviceName ?? "Windows default output"}");
            if (report.SampleRate > 0)
                detail.AppendLine($"Loopback format: {report.SampleRate} Hz, {report.Channels} ch, 32-bit float");
            detail.AppendLine();
            detail.AppendLine(report.Detail);

            var attenuationStep = report.Steps.FirstOrDefault(s => s.Key == "atten_1k");
            var bassStep = report.Steps.FirstOrDefault(s => s.Key == "bass_freq");
            var volumeStep = report.Steps.FirstOrDefault(s => s.Key == "volume_1k");
            var gainChangeMeasured = attenuationStep?.Passed == true;
            var bassMeasured = bassStep?.Passed == true;
            var volumeMeasured = volumeStep?.Passed == true;
            var independent = bassMeasured && volumeMeasured;
            var processingVerified = gainChangeMeasured && settings.AnyProcessingEnabled;

            if (processingVerified)
            {
                _verifiedConfigFingerprint = ComputeConfigFingerprint(configuration.Text);
                _lastVerifiedUtc = DateTime.UtcNow;
                _lastMeasuredDeltaDb = attenuationStep?.DeltaDb;
            }

            if (!report.Ran)
            {
                detail.AppendLine();
                detail.AppendLine("The loopback test could not run. " +
                    "Close exclusive-mode audio applications and make sure the default output device is active.");
            }
            else if (!gainChangeMeasured)
            {
                detail.AppendLine();
                detail.AppendLine("The -18 dB step did not produce a reliable measured change. A written configuration file alone does not process audio. Check Equalizer APO's Device Selector/Configurator for this output, make sure Windows audio enhancements are enabled, and reboot after installing Equalizer APO. If another application is in exclusive mode, close it and retry.");
            }
            else if (!bassMeasured || !volumeMeasured)
            {
                detail.AppendLine();
                detail.AppendLine("The preamp path is verified, but a bass or volume measurement did not match the expected value. See the measured steps above.");
            }

            detail.AppendLine();
            detail.AppendLine("Measured values in this report come from WASAPI loopback. Expected values come from the DSP filter math and are predictions, not measurements.");

            var reportPath = WriteTestReport(detail, report, installation, apo, settings,
                gainChangeMeasured, bassMeasured, volumeMeasured, independent, processingVerified);

            var statusState = !settings.AnyProcessingEnabled
                ? EngineState.Bypassed
                : correctEndpoint ? EngineState.Ready : EngineState.ApoNotEnabled;
            var status = BuildStatus(statusState, installation, true, store.IsIntegrationActive,
                File.Exists(installation.ManagedConfigPath), configuration.Metrics,
                processingVerified
                    ? "Processing verified by measured loopback"
                    : "Configuration written · processing not measured",
                processingVerified ? null : "The DSP file is present, but no measured -18 dB change was observed. See the self-test report in the diagnostics folder.",
                DateTime.UtcNow, settings, apo,
                BuildVerificationText(settings.AnyProcessingEnabled, apo, processingVerified,
                    _lastMeasuredDeltaDb, _lastVerifiedUtc),
                processingVerified, _lastVerifiedUtc, _lastMeasuredDeltaDb);
            Publish(status);

            _log?.Info(processingVerified
                ? $"Audio self-test PASSED: measured {attenuationStep!.DeltaDb:+0.0;-0.0;0.0} dB for the -18 dB step; bass {(bassMeasured ? "pass" : "fail")}, volume {(volumeMeasured ? "pass" : "fail")}."
                : "Audio self-test did not measure a working DSP path.");

            var result = new AudioEngineTestResult(
                true,
                correctEndpoint,
                true,
                processingVerified,
                gainChangeMeasured,
                detail.ToString().TrimEnd(),
                report.DeviceName,
                report.DeviceId,
                attenuationStep?.DeltaDb,
                -18.0,
                report.Steps,
                bassMeasured,
                volumeMeasured,
                independent);
            return result with { ReportPath = reportPath };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Error("Audio engine self-test failed.", ex);
            detail.AppendLine();
            detail.AppendLine($"The self-test failed: {ex.Message}");
            return new AudioEngineTestResult(true, false, false, false, false,
                detail.ToString().TrimEnd());
        }
        finally
        {
            _verificationInProgress = false;
            _applyGate.Release();
        }
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

    private static string BuildApoDetail(ApoAssessment apo)
    {
        var target = apo.TargetDeviceName ?? apo.Scope;
        return "Equalizer APO is installed and Boost Studio's configuration was written, but Windows has no " +
               $"Equalizer APO APO registered on {target}. Open Equalizer APO's Device Selector (or Configurator), " +
               "enable the APO for the output you actually listen on, then press Recheck. A written configuration " +
               "file alone does not process audio.";
    }

    private static string BuildVerificationText(bool processing, ApoAssessment apo,
        bool processingVerified = false, double? measuredDeltaDb = null, DateTime? verifiedUtc = null)
    {
        if (!processing)
            return "Configuration written as bypass; no processing expected.";

        if (processingVerified)
        {
            var measured = measuredDeltaDb is { } delta
                ? $" The -18 dB test measured {delta:+0.0;-0.0;0.0} dB."
                : string.Empty;
            var when = verifiedUtc is { } utc
                ? utc.ToLocalTime().ToString("HH:mm:ss")
                : "this session";
            return $"Processing verified by WASAPI loopback at {when}.{measured}";
        }

        if (!apo.Checked)
            return "Configuration written; APO association could not be checked. Audible processing was not measured.";

        if (!apo.TargetApoEnabled)
            return $"Configuration written; Equalizer APO is not registered on {apo.Scope}. Audible processing was not verified.";

        return "Configuration written; Equalizer APO is attached to the output, but audible processing has not been measured for this configuration. Run Test audio engine in Settings.";
    }

    internal static string ComputeConfigFingerprint(string configText)
    {
        var commands = configText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'));

        var normalized = string.Join("\n", commands);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash);
    }

    private static string BuildFlatTestConfig() =>
        "# Boost Studio self-test reference: no filter or preamp gain." + "\n" +
        "Channel: all" + "\n" +
        "Preamp: 0.0 dB" + "\n";

    private static AppSettings CloneSettings(AppSettings source)
    {
        var copy = AppSettings.CreateDefault();
        copy.CopyFrom(source);
        return copy;
    }

    private static AppSettings BuildVolumeTestSettings(AppSettings settings)
    {
        var copy = CloneSettings(settings);
        copy.ProcessingEnabled = true;
        copy.AutoHeadroom = false;
        copy.Bass.Enabled = false;
        copy.Equalizer.Enabled = false;
        copy.Equalizer.GainsDb = new double[EqConstants.BandCount];
        copy.Volume.Enabled = true;
        copy.Volume.Loudness = 0;
        copy.Volume.AutoGainControl = false;
        if (copy.Volume.EffectiveBoostDb < 0.01)
        {
            copy.Volume.BoostPercent = 50;
            copy.Volume.CeilingDb = 12;
            copy.Volume.PreampDb = 0;
        }

        return copy;
    }

    private static AppSettings BuildBassTestSettings(AppSettings settings)
    {
        var copy = CloneSettings(settings);
        copy.ProcessingEnabled = true;
        // Exercise the default volume-preserving path: auto headroom is on but the
        // bass shelf is excluded, so the 1 kHz measurement must not be attenuated.
        copy.AutoHeadroom = true;
        copy.Bass.ProtectHeadroom = false;
        copy.Volume.Enabled = false;
        copy.Volume.Loudness = 0;
        copy.Equalizer.Enabled = false;
        copy.Equalizer.GainsDb = new double[EqConstants.BandCount];
        copy.Bass.Enabled = true;
        if (copy.Bass.EffectiveGainDb < 0.01)
        {
            copy.Bass.Intensity = 55;
            copy.Bass.MaxGainDb = 20;
            copy.Bass.FrequencyHz = 55;
            copy.Bass.ShelfQ = 0.55;
            copy.Bass.PresetName = "Deep Bass";
        }

        return copy;
    }

    /// <summary>Adds an offset to the existing Preamp line, or inserts one before the first command.</summary>
    public static string AddPreampOffset(string configText, double offsetDb)
    {
        var lines = configText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (!trimmed.StartsWith("Preamp:", StringComparison.OrdinalIgnoreCase))
                continue;

            var valueText = trimmed[7..].Replace("dB", "", StringComparison.OrdinalIgnoreCase).Trim();
            if (double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                lines[i] = $"Preamp: {(value + offsetDb).ToString("0.0", CultureInfo.InvariantCulture)} dB";
                return string.Join("\n", lines);
            }
        }

        var insertAt = lines.FindIndex(line =>
        {
            var trimmed = line.Trim();
            return trimmed.Length > 0 && !trimmed.StartsWith('#');
        });
        if (insertAt < 0) insertAt = lines.Count;
        lines.Insert(insertAt,
            $"Preamp: {offsetDb.ToString("0.0", CultureInfo.InvariantCulture)} dB");
        return string.Join("\n", lines);
    }

    private void TryWriteRestoreMarker(string path, string original)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.RuntimeDirectory);
            File.WriteAllText(path, original, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            _log?.Warn($"Could not create the self-test restore marker: {ex.Message}");
        }
    }

    private void RestoreManagedConfig(EqualizerApoConfigStore store, string original, string restoreMarker)
    {
        var restored = string.IsNullOrWhiteSpace(original)
            ? EqualizerApoConfigStore.ManagedPlaceholder
            : original;
        try
        {
            store.WriteManagedConfig(restored);
        }
        catch (Exception ex)
        {
            _log?.Error("Could not restore the original DSP configuration after the self-test.", ex);
            try
            {
                File.WriteAllText(store.ManagedPath, restored, new UTF8Encoding(false));
            }
            catch (Exception fallback)
            {
                _log?.Error("The fallback restore of the DSP configuration also failed.", fallback);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(restoreMarker))
                    File.Delete(restoreMarker);
            }
            catch
            {
                // A stale marker is handled by TryRecoverInterruptedSelfTest next launch.
            }
        }
    }

    private void TryRecoverInterruptedSelfTest(EqualizerApoInstallation installation)
    {
        if (_verificationInProgress)
            return;

        var marker = Path.Combine(AppPaths.RuntimeDirectory, "eapo-selftest-restore.txt");
        if (!File.Exists(marker))
            return;

        try
        {
            var original = File.ReadAllText(marker, Encoding.UTF8);
            var store = new EqualizerApoConfigStore(installation, _backupDirectory, _log);
            store.WriteManagedConfig(string.IsNullOrWhiteSpace(original)
                ? EqualizerApoConfigStore.ManagedPlaceholder
                : original);
            File.Delete(marker);
            _log?.Warn("Restored the Equalizer APO configuration after an interrupted self-test.");
        }
        catch (Exception ex)
        {
            _log?.Warn($"Could not recover from an interrupted self-test: {ex.Message}");
        }
    }

    private string? WriteTestReport(StringBuilder detail, AudioTestReport report,
        EqualizerApoInstallation installation, ApoAssessment apo, AppSettings settings,
        bool gainMeasured, bool bassMeasured, bool volumeMeasured, bool independent,
        bool processingVerified)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DiagnosticsDirectory);
            var path = Path.Combine(AppPaths.DiagnosticsDirectory,
                $"audio-self-test-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            var text = new StringBuilder();
            text.AppendLine("Boost Studio audio self-test");
            text.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            text.AppendLine($"Boost Studio {AppInfo.Version} on {Environment.OSVersion.VersionString}");
            text.AppendLine($"Equalizer APO: {installation.Version ?? "unknown"} at {installation.InstallDirectory}");
            text.AppendLine($"Managed config: {installation.ManagedConfigPath}");
            text.AppendLine($"APO scope: {apo.Scope}; target APO enabled: {(apo.TargetApoEnabled ? "yes" : "no")}");
            text.AppendLine($"Loopback device: {report.DeviceName ?? "Windows default output"}");
            text.AppendLine($"Loopback format: {report.SampleRate} Hz, {report.Channels} ch");
            text.AppendLine($"Current settings: Bass={settings.Bass.Enabled} ({settings.Bass.EffectiveGainDb:0.0} dB at {settings.Bass.FrequencyHz:0.#} Hz), " +
                            $"Volume={settings.Volume.Enabled} ({settings.Volume.EffectiveBoostDb:0.0} dB), " +
                            $"AutoHeadroom={settings.AutoHeadroom}");
            text.AppendLine();
            text.AppendLine(detail.ToString());
            text.AppendLine();
            text.AppendLine("Verdict:");
            text.AppendLine($"  Equalizer APO detected: yes");
            text.AppendLine($"  APO associated with target output: {(apo.TargetApoEnabled ? "yes" : "no")}");
            text.AppendLine($"  Configuration written and restored: yes");
            text.AppendLine($"  Loopback captured: {(report.LoopbackCaptured ? "yes" : "no")}");
            text.AppendLine($"  -18 dB test observed: {(gainMeasured ? "yes" : "no")}");
            text.AppendLine($"  Bass response measured: {(bassMeasured ? "yes" : "no")}");
            text.AppendLine($"  Volume gain measured: {(volumeMeasured ? "yes" : "no")}");
            text.AppendLine($"  Bass and volume independent: {(independent ? "yes" : "no")}");
            text.AppendLine($"  Processing verified for current configuration: {(processingVerified ? "yes" : "no")}");
            text.AppendLine();
            text.AppendLine("All level changes above are real WASAPI loopback measurements. Expected values are DSP predictions, not measurements.");
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
            return path;
        }
        catch (Exception ex)
        {
            _log?.Warn($"Could not write the audio self-test report: {ex.Message}");
            return null;
        }
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
        string? verificationText = null,
        bool processingVerified = false,
        DateTime? verifiedUtc = null,
        double? measuredDeltaDb = null)
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
            verificationText,
            processingVerified,
            verifiedUtc,
            measuredDeltaDb,
            apo?.SuggestedApoDeviceName,
            apo?.SuggestedApoDeviceId);
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

        _verifier.Dispose();

        // A debounced apply may still be unwinding and will release the gate in its
        // finally block. The semaphore is process-lifetime state; disposing it here
        // can turn a harmless shutdown race into an ObjectDisposedException.
    }
}
