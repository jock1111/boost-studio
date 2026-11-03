using BoostStudio.Core.Storage;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace BoostStudio.Core.Audio;

public enum TestToneKind
{
    Bass,
    Level,
    Sweep
}

/// <summary>
/// Real shared-mode playback used for troubleshooting. Because it plays through the
/// Windows audio engine, Equalizer APO processes it exactly like other applications' audio.
/// </summary>
public sealed class TestToneService : IDisposable
{
    private readonly object _gate = new();
    private readonly ILogService? _log;
    private WaveOutEvent? _output;
    private TestSignalProvider? _provider;
    private bool _disposed;

    public TestToneService(ILogService? log = null)
    {
        _log = log;
    }

    /// <summary>Raised when a test signal finishes naturally or is stopped by the device layer.</summary>
    public event EventHandler? PlaybackEnded;

    public bool IsPlaying
    {
        get { lock (_gate) return _output is not null; }
    }

    public (bool Success, string? Error) Play(TestToneKind kind)
    {
        lock (_gate)
        {
            if (_disposed) return (false, "The test tone service is shutting down.");
            StopInternal();

            try
            {
                _provider = new TestSignalProvider(kind);
                var output = new WaveOutEvent
                {
                    DesiredLatency = 120,
                    NumberOfBuffers = 3
                };
                output.PlaybackStopped += OnPlaybackStopped;
                output.Init(_provider);
                _output = output;
                output.Play();
                _log?.Info($"Test signal started: {kind}.");
                return (true, null);
            }
            catch (Exception ex)
            {
                _log?.Error("Could not play test signal.", ex);
                StopInternal();
                return (false, $"Could not play the test signal: {ex.Message}");
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopInternal();
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        WaveOutEvent? output;
        lock (_gate)
        {
            if (sender != _output) return;
            output = _output;
            _output = null;
            _provider = null;
        }

        if (output is null) return;
        try { output.PlaybackStopped -= OnPlaybackStopped; } catch { /* ignore */ }
        try { output.Dispose(); } catch { /* ignore */ }

        try { PlaybackEnded?.Invoke(this, EventArgs.Empty); }
        catch { /* subscriber failure must not escape the audio callback */ }
    }

    private void StopInternal()
    {
        var output = _output;
        _output = null;
        _provider = null;

        if (output is null) return;

        try { output.PlaybackStopped -= OnPlaybackStopped; } catch { /* ignore */ }
        try { output.Stop(); } catch { /* ignore */ }
        try { output.Dispose(); } catch { /* ignore */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            StopInternal();
        }
    }

    private sealed class TestSignalProvider : ISampleProvider
    {
        private readonly TestToneKind _kind;
        private readonly int _totalSamples;
        private readonly double _fadeSamples;
        private long _position;
        private double _phase;
        private readonly double _durationSeconds;
        private readonly double _startFrequency;
        private readonly double _endFrequency;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        public TestSignalProvider(TestToneKind kind)
        {
            _kind = kind;
            _durationSeconds = kind == TestToneKind.Sweep ? 8.0 : 4.0;
            _totalSamples = (int)(_durationSeconds * WaveFormat.SampleRate);
            _fadeSamples = WaveFormat.SampleRate * 0.06;
            _startFrequency = 30;
            _endFrequency = 18000;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            if (_position >= _totalSamples)
                return 0;

            var remaining = (int)(_totalSamples - _position);
            var frames = Math.Min(count / 2, remaining);
            for (var i = 0; i < frames; i++)
            {
                var time = (double)_position / WaveFormat.SampleRate;
                var progress = (double)_position / _totalSamples;

                double frequency;
                if (_kind == TestToneKind.Bass) frequency = 60;
                else if (_kind == TestToneKind.Level) frequency = 1000;
                else frequency = _startFrequency * Math.Pow(_endFrequency / _startFrequency, progress);

                _phase += 2 * Math.PI * frequency / WaveFormat.SampleRate;
                if (_phase > 2 * Math.PI * 1000) _phase -= 2 * Math.PI * 1000;

                var fadeIn = Math.Min(1.0, _position / _fadeSamples);
                var fadeOut = Math.Min(1.0, (_totalSamples - _position) / _fadeSamples);
                var envelope = Math.Min(fadeIn, fadeOut);

                var amplitude = _kind == TestToneKind.Sweep ? 0.13 : 0.16;
                var value = Math.Sin(_phase) * amplitude * envelope;
                if (_kind == TestToneKind.Bass)
                    value += Math.Sin(_phase * 2) * amplitude * 0.12 * envelope;

                var sample = (float)Math.Clamp(value, -0.5, 0.5);
                buffer[offset + i * 2] = sample;
                buffer[offset + i * 2 + 1] = sample;
                _position++;
            }

            return frames * 2;
        }
    }
}
