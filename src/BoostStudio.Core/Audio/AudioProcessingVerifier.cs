using System.Diagnostics;
using System.Globalization;
using System.Text;
using BoostStudio.Core.Storage;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace BoostStudio.Core.Audio;

/// <summary>
/// One controlled tone measurement. The caller supplies the exact Equalizer APO
/// configuration that must be active while the tone is played, so the same code can
/// verify a preamp change, a bass shelf or a volume boost.
/// </summary>
public sealed record AudioTestStep(
    string Key,
    string Label,
    string ConfigText,
    double FrequencyHz,
    string? BaselineKey = null,
    double? ExpectedDeltaDb = null,
    double ToleranceDb = 2.0,
    double Amplitude = 0.08);

/// <summary>Measured result for one tone step. Levels are real loopback measurements.</summary>
public sealed record AudioTestStepResult(
    string Key,
    string Label,
    double FrequencyHz,
    double ToneLevelDb,
    double BroadbandRmsDb,
    double PeakDb,
    bool ToneDetected,
    double? DeltaDb,
    double? ExpectedDeltaDb,
    double? ToleranceDb,
    bool? Passed,
    string? Error);

/// <summary>
/// Result of a complete self-test. <see cref="LoopbackCaptured"/> and every level in
/// <see cref="Steps"/> are measured from WASAPI loopback; they are not DSP predictions.
/// </summary>
public sealed record AudioTestReport(
    bool Ran,
    bool Success,
    bool LoopbackCaptured,
    string? DeviceId,
    string? DeviceName,
    int SampleRate,
    int Channels,
    IReadOnlyList<AudioTestStepResult> Steps,
    string Detail);

/// <summary>
/// Plays a safe steady tone through the Windows shared-mode audio engine and captures the
/// same endpoint with WASAPI loopback. Because Equalizer APO sits in that shared-mode path,
/// a measured level change proves that the APO is actually processing this machine's audio
/// rather than merely having a configuration file written to disk.
///
/// The verifier keeps every test tone at or below -14 dBFS peak and never enables more
/// than +6 dB of gain, so even the boosted volume step stays around -16 dBFS peak.
/// The caller is responsible for restoring the user's configuration afterwards.
/// </summary>
public sealed class AudioProcessingVerifier : IDisposable
{
    public const double DefaultAmplitude = 0.08;
    private const int SettleMilliseconds = 700;
    private const int LeadInMilliseconds = 400;
    private const int CollectMilliseconds = 900;
    private const int TailMilliseconds = 120;

    private readonly ILogService? _log;
    private bool _disposed;

    public AudioProcessingVerifier(ILogService? log = null)
    {
        _log = log;
    }

    public async Task<AudioTestReport> RunAsync(
        string? deviceId,
        IReadOnlyList<AudioTestStep> steps,
        Func<string, CancellationToken, Task> applyConfig,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (steps.Count == 0)
            return Failed("No test steps were supplied.", null, null, 0, 0);

        var results = new List<AudioTestStepResult>(steps.Count);
        string? deviceName = null;
        string? resolvedId = null;
        var sampleRate = 0;
        var channels = 0;
        var loopbackCaptured = false;
        var detail = new StringBuilder();

        try
        {
            for (var index = 0; index < steps.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var step = steps[index];

                var measurement = await MeasureStepAsync(deviceId, step, applyConfig, cancellationToken)
                    .ConfigureAwait(false);

                if (measurement.DeviceName is not null && deviceName is null)
                {
                    deviceName = measurement.DeviceName;
                    resolvedId = measurement.DeviceId;
                    sampleRate = measurement.SampleRate;
                    channels = measurement.Channels;
                }

                loopbackCaptured |= measurement.Samples > 0;
                results.Add(measurement.Result);
                detail.AppendLine($"{index + 1}. {step.Label}: {FormatMeasurement(measurement.Result)}");
                if (measurement.Result.Error is not null)
                    detail.AppendLine($"   Error: {measurement.Result.Error}");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Error("Audio processing verification failed.", ex);
            return Failed(ex.Message, deviceName, resolvedId, sampleRate, channels);
        }

        // Resolve deltas against the named baseline step. A missing baseline becomes an
        // explicit failure instead of silently comparing unrelated measurements.
        var byKey = results.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            var step = steps[i];
            if (step.BaselineKey is null)
                continue;

            if (!byKey.TryGetValue(step.BaselineKey, out var baseline) || !baseline.ToneDetected)
            {
                results[i] = result with
                {
                    Error = $"Baseline '{step.BaselineKey}' was not measured or the tone was not detected.",
                    Passed = false
                };
                continue;
            }

            var delta = result.ToneLevelDb - baseline.ToneLevelDb;
            var passed = result.ToneDetected && result.Error is null &&
                         step.ExpectedDeltaDb is { } expected &&
                         Math.Abs(delta - expected) <= step.ToleranceDb;

            results[i] = result with
            {
                DeltaDb = delta,
                ExpectedDeltaDb = step.ExpectedDeltaDb,
                ToleranceDb = step.ExpectedDeltaDb is null ? null : step.ToleranceDb,
                Passed = step.ExpectedDeltaDb is null ? null : passed
            };
        }

        var success = results.All(r => r.ToneDetected && r.Error is null && r.Passed != false);
        var measured = results
            .Where(r => r.DeltaDb is not null)
            .Select(r => $"{r.Label}: measured {r.DeltaDb:+0.0;-0.0;0.0} dB, expected {r.ExpectedDeltaDb:+0.0;-0.0;0.0} dB")
            .ToArray();
        if (measured.Length > 0)
        {
            detail.AppendLine();
            detail.AppendLine("Measured versus expected:");
            foreach (var line in measured)
                detail.AppendLine("  " + line);
        }

        return new AudioTestReport(
            true,
            success,
            loopbackCaptured,
            resolvedId,
            deviceName,
            sampleRate,
            channels,
            results,
            detail.ToString().TrimEnd());
    }

    private async Task<StepMeasurement> MeasureStepAsync(
        string? deviceId,
        AudioTestStep step,
        Func<string, CancellationToken, Task> applyConfig,
        CancellationToken cancellationToken)
    {
        await applyConfig(step.ConfigText, cancellationToken).ConfigureAwait(false);
        await Task.Delay(SettleMilliseconds, cancellationToken).ConfigureAwait(false);

        using var enumerator = new MMDeviceEnumerator();
        using var device = OpenDevice(enumerator, deviceId);
        if (device is null)
            return StepMeasurement.Failed(step, "No active Windows render device is available.");

        var deviceName = SafeFriendlyName(device);
        var resolvedId = SafeId(device);

        try
        {
            using var capture = new WasapiLoopbackCapture(device);
            var format = capture.WaveFormat;
            if (format.BitsPerSample != 32)
                return StepMeasurement.Failed(step,
                    $"Loopback format is {format.BitsPerSample}-bit; this test needs the 32-bit float Windows mix format.",
                    deviceName, resolvedId);

            var state = new CaptureState();
            capture.DataAvailable += (_, e) =>
            {
                if (!state.Collecting || e.BytesRecorded <= 0)
                    return;

                try
                {
                    AppendMono(e.Buffer, e.BytesRecorded, format, state);
                }
                catch (Exception ex)
                {
                    _log?.Warn($"Could not read a loopback buffer during self-test: {ex.Message}");
                }
            };

            capture.StartRecording();
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);

            using var output = new WasapiOut(device, AudioClientShareMode.Shared, false, 100);
            var provider = new ToneWaveProvider(format, step.FrequencyHz,
                step.Amplitude <= 0 ? DefaultAmplitude : Math.Clamp(step.Amplitude, 0.001, 0.20));
            output.Init(provider);
            output.Play();

            await Task.Delay(LeadInMilliseconds, cancellationToken).ConfigureAwait(false);
            state.Collecting = true;
            await Task.Delay(CollectMilliseconds, cancellationToken).ConfigureAwait(false);
            state.Collecting = false;

            output.Stop();
            await Task.Delay(TailMilliseconds, cancellationToken).ConfigureAwait(false);
            capture.StopRecording();

            var samples = state.Snapshot();
            if (samples.Length < format.SampleRate / 5)
            {
                return StepMeasurement.Failed(step,
                    "Loopback returned too little audio. Pause other audio, check the output device, and try again.",
                    deviceName, resolvedId, format.SampleRate, format.Channels);
            }

            var toneDb = MeasureToneLevelDb(samples, format.SampleRate, step.FrequencyHz);
            var broadbandDb = MeasureBroadbandRmsDb(samples);
            var peakDb = MeasurePeakDb(samples);
            var detected = toneDb > -80 && double.IsFinite(toneDb);
            var result = new AudioTestStepResult(
                step.Key,
                step.Label,
                step.FrequencyHz,
                toneDb,
                broadbandDb,
                peakDb,
                detected,
                null,
                null,
                null,
                null,
                detected ? null : "The test tone was not detected in the loopback signal.");

            return new StepMeasurement(result, deviceName, resolvedId, format.SampleRate,
                format.Channels, samples.Length);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Error($"Audio self-test step '{step.Label}' failed.", ex);
            return StepMeasurement.Failed(step, ex.Message, deviceName, resolvedId);
        }
    }

    /// <summary>
    /// Coherently measures the level of a known sine in a loopback buffer. Quadrature
    /// detection rejects unrelated broadband audio and the exact-cycle window avoids
    /// spectral leakage. The result is dBFS RMS for that single frequency.
    /// </summary>
    public static double MeasureToneLevelDb(IReadOnlyList<float> samples, int sampleRate, double frequencyHz)
    {
        if (samples.Count == 0 || sampleRate <= 0 || frequencyHz <= 0)
            return double.NegativeInfinity;

        var availableCycles = (int)Math.Floor(samples.Count * frequencyHz / sampleRate);
        if (availableCycles < 1)
            return double.NegativeInfinity;

        var length = Math.Min(samples.Count,
            (int)Math.Round(availableCycles * sampleRate / frequencyHz, MidpointRounding.AwayFromZero));
        if (length < 64)
            return double.NegativeInfinity;

        double inPhase = 0, quadrature = 0;
        for (var i = 0; i < length; i++)
        {
            var phase = 2 * Math.PI * frequencyHz * i / sampleRate;
            var sample = samples[i];
            inPhase += sample * Math.Cos(phase);
            quadrature += sample * Math.Sin(phase);
        }

        var amplitude = 2 * Math.Sqrt(inPhase * inPhase + quadrature * quadrature) / length;
        var rms = amplitude / Math.Sqrt(2);
        return 20 * Math.Log10(Math.Max(rms, 1e-12));
    }

    public static double MeasureBroadbandRmsDb(IReadOnlyList<float> samples)
    {
        if (samples.Count == 0) return double.NegativeInfinity;
        double sum = 0;
        for (var i = 0; i < samples.Count; i++)
            sum += samples[i] * (double)samples[i];
        return 10 * Math.Log10(Math.Max(sum / samples.Count, 1e-24));
    }

    public static double MeasurePeakDb(IReadOnlyList<float> samples)
    {
        if (samples.Count == 0) return double.NegativeInfinity;
        double peak = 0;
        for (var i = 0; i < samples.Count; i++)
            peak = Math.Max(peak, Math.Abs(samples[i]));
        return 20 * Math.Log10(Math.Max(peak, 1e-12));
    }

    private static MMDevice? OpenDevice(MMDeviceEnumerator enumerator, string? deviceId)
    {
        try
        {
            return string.IsNullOrWhiteSpace(deviceId)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : enumerator.GetDevice(deviceId);
        }
        catch
        {
            return null;
        }
    }

    private static void AppendMono(byte[] buffer, int bytesRecorded, WaveFormat format, CaptureState state)
    {
        var channels = Math.Max(1, format.Channels);
        var bytesPerSample = Math.Max(1, format.BitsPerSample / 8);
        var frameSize = Math.Max(1, format.BlockAlign);
        var frames = bytesRecorded / frameSize;
        if (frames <= 0) return;

        var mono = new float[frames];
        for (var frame = 0; frame < frames; frame++)
        {
            var offset = frame * frameSize;
            var sum = 0f;
            for (var channel = 0; channel < channels; channel++)
                sum += ReadSample(buffer, offset + channel * bytesPerSample, format);
            mono[frame] = sum / channels;
        }

        state.Add(mono);
    }

    private static float ReadSample(byte[] buffer, int offset, WaveFormat format)
    {
        if (offset < 0 || offset + format.BitsPerSample / 8 > buffer.Length)
            return 0;

        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
            return Math.Clamp(BitConverter.ToSingle(buffer, offset), -1.2f, 1.2f);

        if (format.Encoding == WaveFormatEncoding.Pcm)
        {
            if (format.BitsPerSample == 16)
                return BitConverter.ToInt16(buffer, offset) / 32768f;
            if (format.BitsPerSample == 24)
            {
                var value = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
                if ((value & 0x800000) != 0) value |= unchecked((int)0xFF000000);
                return value / 8388608f;
            }
            if (format.BitsPerSample == 32)
                return BitConverter.ToInt32(buffer, offset) / 2147483648f;
        }

        // Windows' shared-mode mix format is normally 32-bit IEEE float, sometimes
        // wrapped in WAVEFORMATEXTENSIBLE. Treat 32-bit data as float in that case.
        if (format.BitsPerSample == 32)
            return Math.Clamp(BitConverter.ToSingle(buffer, offset), -1.2f, 1.2f);

        return 0;
    }

    private static string? SafeFriendlyName(MMDevice device)
    {
        try { return device.FriendlyName; }
        catch { return null; }
    }

    private static string? SafeId(MMDevice device)
    {
        try { return device.ID; }
        catch { return null; }
    }

    private static string FormatMeasurement(AudioTestStepResult result)
    {
        var text = new StringBuilder();
        text.Append(result.FrequencyHz.ToString("0", CultureInfo.InvariantCulture)).Append(" Hz, ");
        text.Append(result.ToneLevelDb.ToString("0.0", CultureInfo.InvariantCulture)).Append(" dBFS");
        if (result.DeltaDb is { } delta)
            text.Append(", delta ").Append(delta.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture)).Append(" dB");
        if (result.Passed is { } passed)
            text.Append(passed ? " PASS" : " FAIL");
        else if (!result.ToneDetected)
            text.Append(" NOT DETECTED");
        return text.ToString();
    }

    private static AudioTestReport Failed(string detail, string? deviceName, string? deviceId,
        int sampleRate, int channels) =>
        new(false, false, false, deviceId, deviceName, sampleRate, channels,
            Array.Empty<AudioTestStepResult>(), detail);

    public void Dispose()
    {
        _disposed = true;
    }

    private sealed class CaptureState
    {
        private readonly object _gate = new();
        private readonly List<float> _samples = new(48000 * 4);
        public volatile bool Collecting;

        public void Add(float[] samples)
        {
            lock (_gate)
                _samples.AddRange(samples);
        }

        public float[] Snapshot()
        {
            lock (_gate)
                return _samples.ToArray();
        }
    }

    private sealed record StepMeasurement(
        AudioTestStepResult Result,
        string? DeviceName,
        string? DeviceId,
        int SampleRate,
        int Channels,
        int Samples)
    {
        public static StepMeasurement Failed(AudioTestStep step, string error,
            string? deviceName = null, string? deviceId = null,
            int sampleRate = 0, int channels = 0) =>
            new(new AudioTestStepResult(step.Key, step.Label, step.FrequencyHz,
                double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity,
                false, null, null, null, false, error),
                deviceName, deviceId, sampleRate, channels, 0);
    }

    /// <summary>
    /// A continuous sine at a low, fixed amplitude. Reads are always the endpoint's exact
    /// 32-bit float mix format so WASAPI shared mode accepts the stream unchanged.
    /// </summary>
    private sealed class ToneWaveProvider : IWaveProvider
    {
        private readonly int _channels;
        private readonly int _blockAlign;
        private readonly double _delta;
        private readonly float _amplitude;
        private double _phase;

        public ToneWaveProvider(WaveFormat format, double frequencyHz, double amplitude)
        {
            WaveFormat = format;
            _channels = Math.Max(1, format.Channels);
            _blockAlign = Math.Max(1, format.BlockAlign);
            _delta = 2 * Math.PI * frequencyHz / Math.Max(1, format.SampleRate);
            _amplitude = (float)amplitude;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(byte[] buffer, int offset, int count)
        {
            var frames = count / _blockAlign;
            for (var frame = 0; frame < frames; frame++)
            {
                var sample = (float)(Math.Sin(_phase) * _amplitude);
                _phase += _delta;
                if (_phase >= 2 * Math.PI)
                    _phase -= 2 * Math.PI;

                var frameOffset = offset + frame * _blockAlign;
                for (var channel = 0; channel < _channels; channel++)
                    BitConverter.TryWriteBytes(buffer.AsSpan(frameOffset + channel * 4, 4), sample);
            }

            return frames * _blockAlign;
        }
    }
}
