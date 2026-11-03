using System.Globalization;
using System.Windows.Input;
using BoostStudio.App.Infrastructure;
using BoostStudio.Core.Dsp;
using BoostStudio.Core.Models;

namespace BoostStudio.App.ViewModels;

public sealed class VolumeBoosterViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly AutoGainController _autoGain;
    private readonly Action _changed;
    private string _agcStatus = "Off";
    private double[] _loudnessResponse;

    public VolumeBoosterViewModel(AppSettings settings, MonitorViewModel monitor, TestToneViewModel testTone,
        AutoGainController autoGain, Action changed)
    {
        _settings = settings;
        _autoGain = autoGain;
        _changed = changed;
        Monitor = monitor;
        TestTone = testTone;
        _loudnessResponse = CurveSampler.Loudness(settings.Volume);
        ResetAgcCommand = new RelayCommand(ResetAgc, () => AutoGainControl || Math.Abs(_autoGain.TrimDb) > 0.01);
    }

    public MonitorViewModel Monitor { get; }
    public TestToneViewModel TestTone { get; }
    public RelayCommand ResetAgcCommand { get; }

    public bool Enabled
    {
        get => _settings.Volume.Enabled;
        set
        {
            if (_settings.Volume.Enabled == value) return;
            _settings.Volume.Enabled = value;
            OnPropertyChanged(nameof(Enabled), nameof(StatusText), nameof(ActiveLabel), nameof(Description),
                nameof(AgcDescription), nameof(SignalPathText));
            UpdateAgcStatus();
            _changed();
        }
    }

    public int BoostPercent
    {
        get => _settings.Volume.BoostPercent;
        set => SetParameter(() => _settings.Volume.BoostPercent = Math.Clamp(value, 0, 100), nameof(BoostPercent));
    }

    public double CeilingDb
    {
        get => _settings.Volume.CeilingDb;
        set => SetParameter(() => _settings.Volume.CeilingDb = Math.Clamp(Math.Round(value, 1), 3, 24), nameof(CeilingDb));
    }

    public double PreampDb
    {
        get => _settings.Volume.PreampDb;
        set => SetParameter(() => _settings.Volume.PreampDb = Math.Clamp(Math.Round(value, 1), -12, 12), nameof(PreampDb));
    }

    public int Loudness
    {
        get => _settings.Volume.Loudness;
        set => SetParameter(() => _settings.Volume.Loudness = Math.Clamp(value, 0, 100), nameof(Loudness));
    }

    public bool AutoGainControl
    {
        get => _settings.Volume.AutoGainControl;
        set
        {
            if (_settings.Volume.AutoGainControl == value) return;
            _settings.Volume.AutoGainControl = value;
            OnPropertyChanged(nameof(AutoGainControl), nameof(AgcDescription), nameof(StatusText));
            UpdateAgcStatus();
            ResetAgcCommand.RaiseCanExecuteChanged();
            _changed();
        }
    }

    public double EffectiveBoostDb => _settings.Volume.EffectiveBoostDb;
    public string BoostText => $"{EffectiveBoostDb:+0.0;-0.0;0.0} dB";
    public string BoostDescription => $"{BoostPercent}% of a {CeilingDb:0.#} dB ceiling";
    public double HeadroomDb => DspMath.ComputeHeadroomReservationDb(_settings);
    public double RuntimeTrimDb => _autoGain.TrimDb;

    public string EffectivePreampText
    {
        get
        {
            var preamp = EffectiveBoostDb + PreampDb + RuntimeTrimDb - HeadroomDb;
            return $"{preamp:+0.0;-0.0;0.0} dB";
        }
    }

    /// <summary>Honest net gain at the preamp stage after the automatic headroom cost.</summary>
    public string NetGainText => EffectivePreampText;

    public string HeadroomAppliedText => HeadroomDb > 0.05
        ? $"Net gain after {HeadroomDb:0.0} dB of automatic headroom"
        : "No headroom reserved";

    public string SignalPathText
    {
        get
        {
            var parts = new List<string> { BoostText };
            if (Math.Abs(PreampDb) > 0.05) parts.Add($"trim {PreampDb:+0.0;-0.0;0.0}");
            if (HeadroomDb > 0.05) parts.Add($"headroom −{HeadroomDb:0.0}");
            if (Math.Abs(RuntimeTrimDb) > 0.05) parts.Add($"AGC {RuntimeTrimDb:+0.0;-0.0;0.0}");
            return $"Preamp {EffectivePreampText}  ·  " + string.Join("  ·  ", parts);
        }
    }

    public string LoudnessText => Loudness <= 0 ? "Off" : $"{Loudness}% contour";
    public string Description => !Enabled
        ? "Volume Booster is bypassed. No extra boost is applied."
        : $"{BoostText} digital boost · {LoudnessText}";
    public string StatusText => Enabled ? "Volume Booster is active" : "Volume Booster is off";
    public string ActiveLabel => Enabled ? "ACTIVE" : "OFF";
    public string GainRangeText => $"0 – {CeilingDb:0.#} dB";

    public string AgcStatus
    {
        get => _agcStatus;
        private set => SetProperty(ref _agcStatus, value);
    }

    public string AgcDescription
    {
        get
        {
            if (!AutoGainControl) return "Peak AGC is off. Watch the clip indicator when boosting.";
            if (!Enabled) return "Enable Volume Booster to let AGC protect output level.";
            return _autoGain.Describe();
        }
    }

    public double[] LoudnessResponse
    {
        get => _loudnessResponse;
        private set => SetProperty(ref _loudnessResponse, value);
    }

    private void SetParameter(Action apply, string propertyName)
    {
        var before = GetSignature();
        apply();
        if (GetSignature() == before) return;

        OnPropertyChanged(propertyName, nameof(EffectiveBoostDb), nameof(BoostText), nameof(BoostDescription),
            nameof(HeadroomDb), nameof(EffectivePreampText), nameof(NetGainText), nameof(HeadroomAppliedText),
            nameof(SignalPathText), nameof(LoudnessText),
            nameof(Description), nameof(GainRangeText));
        UpdateLoudness();
        if (propertyName == nameof(CeilingDb))
            OnPropertyChanged(nameof(GainRangeText));
        ResetAgcCommand.RaiseCanExecuteChanged();
        _changed();
    }

    private string GetSignature() =>
        $"{_settings.Volume.BoostPercent}|{_settings.Volume.CeilingDb}|{_settings.Volume.PreampDb}|{_settings.Volume.Loudness}";

    public void UpdateAgcStatus()
    {
        AgcStatus = !AutoGainControl ? "Off" : Enabled ? _autoGain.Describe() : "Enable Volume Booster";
        OnPropertyChanged(nameof(AgcDescription), nameof(RuntimeTrimDb), nameof(EffectivePreampText),
            nameof(NetGainText), nameof(HeadroomAppliedText), nameof(SignalPathText));
        ResetAgcCommand.RaiseCanExecuteChanged();
    }

    public void UpdateRuntimeTrim()
    {
        OnPropertyChanged(nameof(RuntimeTrimDb), nameof(EffectivePreampText), nameof(NetGainText),
            nameof(HeadroomAppliedText), nameof(SignalPathText));
        UpdateAgcStatus();
    }

    private void UpdateLoudness()
    {
        LoudnessResponse = CurveSampler.Loudness(_settings.Volume);
    }

    private void ResetAgc()
    {
        _autoGain.Reset();
        UpdateAgcStatus();
    }

    public void ReloadFromSettings()
    {
        OnPropertyChanged(nameof(Enabled), nameof(BoostPercent), nameof(CeilingDb), nameof(PreampDb),
            nameof(Loudness), nameof(AutoGainControl), nameof(EffectiveBoostDb), nameof(BoostText),
            nameof(BoostDescription), nameof(HeadroomDb), nameof(EffectivePreampText), nameof(NetGainText),
            nameof(HeadroomAppliedText), nameof(SignalPathText),
            nameof(LoudnessText), nameof(Description), nameof(StatusText), nameof(ActiveLabel),
            nameof(GainRangeText), nameof(AgcStatus), nameof(AgcDescription));
        UpdateLoudness();
        UpdateAgcStatus();
    }

    public string FormatLevel(double db) =>
        db <= -100 ? "−∞" : db.ToString("0.0", CultureInfo.CurrentCulture);
}
