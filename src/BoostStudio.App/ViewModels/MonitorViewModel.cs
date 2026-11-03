using System.Globalization;
using System.Windows.Threading;
using BoostStudio.App.Infrastructure;
using BoostStudio.Core.Models;

namespace BoostStudio.App.ViewModels;

public sealed class MonitorViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _clipTimer;
    private float[] _levels = Array.Empty<float>();
    private float[] _bassLevels = Array.Empty<float>();
    private double _peakDb = -120;
    private double _peakHoldDb = -120;
    private double _rmsDb = -120;
    private bool _isClipping;
    private bool _clipWarning;
    private bool _isActive;
    private string _deviceName = "No output device";
    private string _formatSummary = "Waiting for audio";
    private string _statusText = "Starting system audio monitoring…";
    private string _dominantBassText = "—";
    private long _clippingSamples;
    private DateTime _lastClipUtc = DateTime.MinValue;
    private bool _warningsEnabled = true;

    public MonitorViewModel()
    {
        _clipTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _clipTimer.Tick += OnClipTimerTick;
    }

    public float[] Levels
    {
        get => _levels;
        private set => SetProperty(ref _levels, value);
    }

    public float[] BassLevels
    {
        get => _bassLevels;
        private set => SetProperty(ref _bassLevels, value);
    }

    public double PeakDb
    {
        get => _peakDb;
        private set
        {
            if (!SetProperty(ref _peakDb, value)) return;
            OnPropertyChanged(nameof(PeakText));
            OnPropertyChanged(nameof(HasSignal));
        }
    }

    public double PeakHoldDb
    {
        get => _peakHoldDb;
        private set
        {
            if (!SetProperty(ref _peakHoldDb, value)) return;
            OnPropertyChanged(nameof(PeakHoldText));
        }
    }

    public double RmsDb
    {
        get => _rmsDb;
        private set
        {
            if (!SetProperty(ref _rmsDb, value)) return;
            OnPropertyChanged(nameof(RmsText));
        }
    }

    public bool IsClipping
    {
        get => _isClipping;
        private set => SetProperty(ref _isClipping, value);
    }

    public bool ClipWarning
    {
        get => _clipWarning;
        private set => SetProperty(ref _clipWarning, value);
    }

    public bool IsActive
    {
        get => _isActive;
        private set => SetProperty(ref _isActive, value);
    }

    public string DeviceName
    {
        get => _deviceName;
        private set => SetProperty(ref _deviceName, value);
    }

    public string FormatSummary
    {
        get => _formatSummary;
        private set => SetProperty(ref _formatSummary, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string DominantBassText
    {
        get => _dominantBassText;
        private set => SetProperty(ref _dominantBassText, value);
    }

    public long ClippingSamples
    {
        get => _clippingSamples;
        private set
        {
            if (!SetProperty(ref _clippingSamples, value)) return;
            OnPropertyChanged(nameof(ClippingSamplesText));
        }
    }

    public string PeakText => FormatDb(PeakDb);
    public string PeakHoldText => FormatDb(PeakHoldDb);
    public string RmsText => FormatDb(RmsDb);
    public string ClippingSamplesText => $"{ClippingSamples:N0}";
    public bool HasSignal => PeakDb > -72;

    public bool WarningsEnabled
    {
        get => _warningsEnabled;
        set
        {
            if (!SetProperty(ref _warningsEnabled, value)) return;
            if (!value)
            {
                ClipWarning = false;
                _clipTimer.Stop();
            }
        }
    }

    public void Update(SpectrumFrame frame)
    {
        Levels = frame.Spectrum;
        BassLevels = frame.BassSpectrum;
        PeakDb = frame.PeakDb;
        PeakHoldDb = frame.PeakHoldDb;
        RmsDb = frame.RmsDb;
        ClippingSamples = frame.ClippingSamples;
        IsClipping = frame.IsClipping;

        DominantBassText = frame.DominantBassHz > 0
            ? $"{frame.DominantBassHz:0} Hz"
            : "—";

        if (frame.IsClipping && WarningsEnabled)
        {
            _lastClipUtc = DateTime.UtcNow;
            ClipWarning = true;
            if (!_clipTimer.IsEnabled)
                _clipTimer.Start();
        }
    }

    public void SetMonitoring(string? deviceName, int sampleRate, int channels, string statusText)
    {
        DeviceName = string.IsNullOrWhiteSpace(deviceName) ? "No output device" : deviceName;
        FormatSummary = sampleRate > 0
            ? $"{sampleRate / 1000.0:0.#} kHz · {channels} ch · WASAPI loopback"
            : "WASAPI loopback";
        StatusText = statusText;
        IsActive = true;
    }

    public void SetStopped(string statusText)
    {
        StatusText = statusText;
        IsActive = false;
        PeakDb = -120;
        RmsDb = -120;
        PeakHoldDb = -120;
        IsClipping = false;
        Levels = Array.Empty<float>();
        BassLevels = Array.Empty<float>();
        DominantBassText = "—";
    }

    public void SetStarting(string statusText)
    {
        StatusText = statusText;
        IsActive = false;
    }

    private void OnClipTimerTick(object? sender, EventArgs e)
    {
        if (DateTime.UtcNow - _lastClipUtc > TimeSpan.FromSeconds(2.2))
        {
            ClipWarning = false;
            _clipTimer.Stop();
        }
    }

    private static string FormatDb(double value) =>
        value <= -100 ? "−∞ dB" : value.ToString("0.0", CultureInfo.CurrentCulture) + " dB";

    public void Dispose()
    {
        _clipTimer.Stop();
        _clipTimer.Tick -= OnClipTimerTick;
    }
}
