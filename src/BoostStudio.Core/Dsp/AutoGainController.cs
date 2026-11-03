namespace BoostStudio.Core.Dsp;

/// <summary>
/// Slow, real-output peak-based automatic gain control. This is deliberately not
/// presented as a sample-accurate limiter: Equalizer APO applies parameter changes
/// on config reload, so this controller reduces gain in conservative 0.5-6 dB steps
/// after measuring actual post-processing loopback peaks.
/// </summary>
public sealed class AutoGainController
{
    private readonly object _gate = new();
    private readonly Func<DateTime> _clock;

    private bool _enabled;
    private double _trimDb;
    private double _lastAttackDb = -120;
    private DateTime _lastAttackUtc = DateTime.MinValue;
    private DateTime _quietSinceUtc = DateTime.MaxValue;
    private DateTime _lastRecoveryUtc = DateTime.MinValue;
    private DateTime _lastFrameUtc = DateTime.MinValue;

    public AutoGainController(Func<DateTime>? clock = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public double TrimDb
    {
        get { lock (_gate) return _trimDb; }
    }

    public bool IsEnabled
    {
        get { lock (_gate) return _enabled; }
    }

    public event EventHandler<double>? TrimChanged;

    public void Configure(bool enabled)
    {
        bool changed;
        double newTrim;
        lock (_gate)
        {
            changed = _enabled != enabled;
            _enabled = enabled;
            newTrim = enabled ? _trimDb : 0;
            if (!enabled)
            {
                _trimDb = 0;
                _quietSinceUtc = DateTime.MaxValue;
                _lastAttackDb = -120;
            }
        }

        if (changed || Math.Abs(newTrim - TrimDb) > 0.001)
        {
            // Never send an unbounded jump. AGC adds its own trim only on top of
            // the user's gain and is always limited to -24..0 dB.
            TrimChanged?.Invoke(this, newTrim);
        }
    }

    public void Reset()
    {
        double old;
        lock (_gate)
        {
            old = _trimDb;
            _trimDb = 0;
            _quietSinceUtc = DateTime.MaxValue;
            _lastAttackDb = -120;
        }

        if (Math.Abs(old) > 0.001)
            TrimChanged?.Invoke(this, 0);
    }

    public void ProcessPeak(double peakDb, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? _clock();
        double? newTrim = null;

        lock (_gate)
        {
            if (!_enabled) return;
            if (now - _lastFrameUtc > TimeSpan.FromSeconds(5))
            {
                _lastFrameUtc = now;
                _lastAttackUtc = DateTime.MinValue;
                _quietSinceUtc = DateTime.MaxValue;
                return;
            }
            _lastFrameUtc = now;

            if (peakDb > -1.0)
            {
                _quietSinceUtc = DateTime.MaxValue;
                if (now - _lastAttackUtc >= TimeSpan.FromMilliseconds(600))
                {
                    var requestedReduction = Math.Clamp(peakDb + 1.0 + 1.0, 1.0, 6.0);
                    var target = Math.Round(Math.Max(-24, _trimDb - requestedReduction) * 2) / 2;
                    if (Math.Abs(target - _trimDb) >= 0.4)
                    {
                        _trimDb = target;
                        _lastAttackUtc = now;
                        _lastRecoveryUtc = now;
                        _lastAttackDb = peakDb;
                        newTrim = _trimDb;
                    }
                }
            }
            else if (peakDb < -14.0)
            {
                if (_quietSinceUtc == DateTime.MaxValue)
                    _quietSinceUtc = now;

                if (now - _quietSinceUtc >= TimeSpan.FromSeconds(10) &&
                    now - _lastRecoveryUtc >= TimeSpan.FromSeconds(2) &&
                    _trimDb < 0)
                {
                    _trimDb = Math.Round(Math.Min(0, _trimDb + 0.5) * 2) / 2;
                    _lastRecoveryUtc = now;
                    newTrim = _trimDb;
                }
            }
            else
            {
                _quietSinceUtc = DateTime.MaxValue;
            }
        }

        if (newTrim.HasValue)
            TrimChanged?.Invoke(this, newTrim.Value);
    }

    public string Describe()
    {
        lock (_gate)
        {
            if (!_enabled) return "Auto Gain is off";
            if (_trimDb <= -0.1) return $"Active · {_trimDb:0.0} dB safety trim";
            if (_lastAttackDb > -1) return "Protecting output";
            return "Listening · no peaks above threshold";
        }
    }
}
