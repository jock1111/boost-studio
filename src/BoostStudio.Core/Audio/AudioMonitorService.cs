using BoostStudio.Core.Dsp;
using BoostStudio.Core.Models;
using BoostStudio.Core.Storage;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace BoostStudio.Core.Audio;

/// <summary>
/// Captures the real post-mix WASAPI loopback stream for spectrum, peak/RMS meters and
/// AGC. It never feeds audio back to the output and has no effect on playback if stopped.
/// </summary>
public sealed class AudioMonitorService : IDisposable
{
    private const int FftSize = SpectrumAnalyzer.FftSize;
    private const int HopSize = 2048;

    private readonly object _sync = new();
    private readonly ILogService? _log;
    private readonly float[] _ring = new float[FftSize];
    private readonly float[] _analysis = new float[FftSize];

    private MMDevice? _device;
    private WasapiLoopbackCapture? _capture;
    private SpectrumAnalyzer? _analyzer;
    private bool _disposed;
    private bool _stopping;

    private int _writePosition;
    private int _filled;
    private int _sinceAnalysis;
    private long _clippingSamples;
    private long _lastClippingSamples;
    private double _peakHoldDb = -120;
    private DateTime _lastFrameUtc = DateTime.MinValue;

    public bool IsRunning { get; private set; }

    public string? DeviceName { get; private set; }

    public string? DeviceId { get; private set; }

    public int SampleRate { get; private set; }

    public int Channels { get; private set; }

    public string? LastError { get; private set; }

    public double PeakHoldDb => _peakHoldDb;

    public event EventHandler<SpectrumFrame>? FrameReady;

    public event EventHandler<string>? MonitorStopped;

    public AudioMonitorService(ILogService? log = null)
    {
        _log = log;
    }

    public string Start(string? deviceId)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopInternal();

            try
            {
                var enumerator = new MMDeviceEnumerator();
                try
                {
                    _device = string.IsNullOrWhiteSpace(deviceId)
                        ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                        : enumerator.GetDevice(deviceId);
                }
                finally
                {
                    enumerator.Dispose();
                }

                DeviceId = _device.ID;
                DeviceName = _device.FriendlyName;
                _capture = new WasapiLoopbackCapture(_device);
                SampleRate = _capture.WaveFormat.SampleRate;
                Channels = _capture.WaveFormat.Channels;
                _analyzer = new SpectrumAnalyzer(SampleRate);
                _writePosition = 0;
                _filled = 0;
                _sinceAnalysis = 0;
                _clippingSamples = 0;
                _lastClippingSamples = 0;
                _peakHoldDb = -120;
                _lastFrameUtc = DateTime.MinValue;
                _capture.DataAvailable += OnDataAvailable;
                _capture.RecordingStopped += OnRecordingStopped;
                _capture.StartRecording();
                IsRunning = true;
                LastError = null;
                _log?.Info($"Audio monitor started: {DeviceName} ({SampleRate} Hz, {Channels} ch).");
                return string.Empty;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _log?.Error("Could not start system audio monitoring.", ex);
                StopInternal();
                return ex.Message;
            }
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        SpectrumFrame? frame = null;
        var localCapture = _capture;
        if (localCapture is null || e.BytesRecorded <= 0)
            return;

        try
        {
            var format = localCapture.WaveFormat;
            var channels = Math.Max(1, format.Channels);
            var bytesPerSample = Math.Max(1, format.BitsPerSample / 8);
            var frameSize = Math.Max(1, format.BlockAlign);
            var frames = e.BytesRecorded / frameSize;

            lock (_sync)
            {
                if (!ReferenceEquals(localCapture, _capture) || _stopping)
                    return;

                for (var frameIndex = 0; frameIndex < frames; frameIndex++)
                {
                    var offset = frameIndex * frameSize;
                    var sum = 0.0f;

                    for (var channel = 0; channel < channels; channel++)
                    {
                        var sampleOffset = offset + channel * bytesPerSample;
                        var sample = ReadSample(e.Buffer, sampleOffset, format);
                        sum += sample;
                        if (Math.Abs(sample) >= 0.9995f)
                            _clippingSamples++;
                    }

                    var mono = sum / channels;
                    _ring[_writePosition] = mono;
                    _writePosition = (_writePosition + 1) % FftSize;
                    if (_filled < FftSize) _filled++;
                    _sinceAnalysis++;

                    if (_filled >= FftSize && _sinceAnalysis >= HopSize)
                    {
                        _sinceAnalysis = 0;
                        CopyChronologicalWindow();
                        var result = _analyzer!.Analyze(_analysis);
                        var now = DateTime.UtcNow;
                        var elapsed = _lastFrameUtc == DateTime.MinValue
                            ? 0
                            : Math.Clamp((now - _lastFrameUtc).TotalSeconds, 0, 0.5);
                        _lastFrameUtc = now;
                        _peakHoldDb = Math.Max(result.PeakDb, _peakHoldDb - elapsed * 10.0);
                        var newClippingSamples = _clippingSamples - _lastClippingSamples;
                        _lastClippingSamples = _clippingSamples;
                        var isClipping = result.PeakDb >= -0.05 || newClippingSamples > 0;
                        frame = new SpectrumFrame(
                            (float[])result.Spectrum.Clone(),
                            (float[])result.Bass.Clone(),
                            result.RmsDb,
                            result.PeakDb,
                            _peakHoldDb,
                            _clippingSamples,
                            isClipping,
                            SampleRate,
                            Channels,
                            result.DominantBassHz,
                            now);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _log?.Error("Audio monitor analysis failed.", ex);
            MonitorStopped?.Invoke(this, $"Audio analysis error: {ex.Message}");
        }

        if (frame is not null)
        {
            try
            {
                FrameReady?.Invoke(this, frame);
            }
            catch (Exception ex)
            {
                _log?.Error("A spectrum frame consumer failed.", ex);
            }
        }
    }

    private void CopyChronologicalWindow()
    {
        // When the ring is full, _writePosition points at the oldest sample.
        var first = _filled < FftSize ? 0 : _writePosition;
        for (var i = 0; i < FftSize; i++)
            _analysis[i] = _ring[(first + i) % FftSize];
    }

    private float ReadSample(byte[] buffer, int offset, WaveFormat format)
    {
        var bytes = format.BitsPerSample / 8;
        if (offset + bytes > buffer.Length) return 0;

        var encoding = format.Encoding;
        if (encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
            return Math.Clamp(BitConverter.ToSingle(buffer, offset), -1.2f, 1.2f);

        if (encoding == WaveFormatEncoding.Pcm)
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

        // WASAPI loopback normally reports 32-bit IEEE float. Extensible formats with
        // 32 bits are treated as float, which is the Windows audio engine mix format.
        if (format.BitsPerSample == 32)
            return Math.Clamp(BitConverter.ToSingle(buffer, offset), -1.2f, 1.2f);

        return 0;
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        lock (_sync)
        {
            if (_stopping || _disposed) return;
            IsRunning = false;
            var message = e.Exception?.Message ?? "Audio monitoring stopped.";
            LastError = message;
            _log?.Warn($"Audio monitor stopped: {message}");
        }

        if (e.Exception is not null)
            MonitorStopped?.Invoke(this, e.Exception.Message);
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (_disposed) return;
            StopInternal();
        }
    }

    private void StopInternal()
    {
        _stopping = true;
        if (_capture is not null)
        {
            try { _capture.DataAvailable -= OnDataAvailable; } catch { /* ignore */ }
            try { _capture.RecordingStopped -= OnRecordingStopped; } catch { /* ignore */ }
            try { _capture.StopRecording(); } catch { /* ignore */ }
            try { _capture.Dispose(); } catch { /* ignore */ }
            _capture = null;
        }

        try { _device?.Dispose(); } catch { /* ignore */ }
        _device = null;
        _analyzer = null;
        IsRunning = false;
        _stopping = false;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            StopInternal();
            _disposed = true;
        }
    }
}
