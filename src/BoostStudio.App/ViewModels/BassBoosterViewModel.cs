using BoostStudio.App.Infrastructure;
using BoostStudio.Core.Models;

namespace BoostStudio.App.ViewModels;

public sealed class BassBoosterViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly Action _changed;
    private bool _suppressPreset;
    private double[] _response;

    public BassBoosterViewModel(AppSettings settings, MonitorViewModel monitor, TestToneViewModel testTone, Action changed)
    {
        _settings = settings;
        _changed = changed;
        Monitor = monitor;
        TestTone = testTone;
        _response = CurveSampler.Bass(settings.Bass);

        ResetCommand = new RelayCommand(ResetToDefaults);
    }

    public MonitorViewModel Monitor { get; }
    public TestToneViewModel TestTone { get; }
    public IReadOnlyList<BassPreset> Presets => BassPresets.All;
    public RelayCommand ResetCommand { get; }

    public bool Enabled
    {
        get => _settings.Bass.Enabled;
        set
        {
            if (_settings.Bass.Enabled == value) return;
            _settings.Bass.Enabled = value;
            OnPropertyChanged(nameof(Enabled), nameof(StatusText), nameof(ActiveLabel), nameof(Description));
            _changed();
        }
    }

    public int Intensity
    {
        get => _settings.Bass.Intensity;
        set => SetBassParameter(() => _settings.Bass.Intensity = Math.Clamp(value, 0, 100),
            nameof(Intensity));
    }

    public double FrequencyHz
    {
        get => _settings.Bass.FrequencyHz;
        set => SetBassParameter(() => _settings.Bass.FrequencyHz = Math.Clamp(Math.Round(value, 1), 20, 250),
            nameof(FrequencyHz));
    }

    public double MaxGainDb
    {
        get => _settings.Bass.MaxGainDb;
        set => SetBassParameter(() => _settings.Bass.MaxGainDb = Math.Clamp(Math.Round(value, 1), 0, 24),
            nameof(MaxGainDb));
    }

    public double ShelfQ
    {
        get => _settings.Bass.ShelfQ;
        set => SetBassParameter(() => _settings.Bass.ShelfQ = Math.Clamp(Math.Round(value, 2), 0.30, 1.40),
            nameof(ShelfQ));
    }

    public double EffectiveGainDb => _settings.Bass.EffectiveGainDb;
    public string GainText => $"{EffectiveGainDb:+0.0;-0.0;0.0} dB";
    public string FrequencyText => $"{_settings.Bass.FrequencyHz:0.#} Hz";
    public string EffectiveGainDescription => $"Low shelf · {EffectiveGainDb:0.0} dB";
    public string QText => _settings.Bass.ShelfQ.ToString("0.00");

    public string Description => !Enabled
        ? "Bass Booster is bypassed. Your shelf settings are retained."
        : EffectiveGainDb <= 0.05
            ? "Intensity is at zero, so the shelf is currently flat."
            : $"{GainText} low shelf at {FrequencyText} · Q {QText}";

    public string StatusText => Enabled ? "Bass Booster is active" : "Bass Booster is off";
    public string ActiveLabel => Enabled ? "ACTIVE" : "OFF";

    public double[] Response
    {
        get => _response;
        private set => SetProperty(ref _response, value);
    }

    public BassPreset? SelectedPreset
    {
        get => BassPresets.All.FirstOrDefault(p =>
            string.Equals(p.Name, _settings.Bass.PresetName, StringComparison.OrdinalIgnoreCase))
            ?? BassPresets.All[^1];
        set
        {
            if (value is null || _suppressPreset) return;
            if (value.Name == BassPresets.Custom)
            {
                _settings.Bass.PresetName = BassPresets.Custom;
            }
            else
            {
                BassPresets.Apply(_settings.Bass, value);
            }

            OnPropertyChanged(nameof(Intensity), nameof(FrequencyHz), nameof(MaxGainDb), nameof(ShelfQ),
                nameof(SelectedPreset), nameof(EffectiveGainDb), nameof(GainText),
                nameof(EffectiveGainDescription), nameof(QText), nameof(FrequencyText),
                nameof(Description), nameof(StatusText));
            UpdateResponse();
            _changed();
        }
    }

    private void SetBassParameter(Action apply, string propertyName)
    {
        var before = GetParameterSignature();
        apply();
        if (GetParameterSignature() == before) return;

        UpdatePresetDetection();
        OnPropertyChanged(propertyName, nameof(SelectedPreset), nameof(EffectiveGainDb), nameof(GainText),
            nameof(FrequencyText), nameof(EffectiveGainDescription), nameof(QText), nameof(Description));
        UpdateResponse();
        _changed();
    }

    private string GetParameterSignature() =>
        $"{_settings.Bass.Intensity}|{_settings.Bass.FrequencyHz}|{_settings.Bass.MaxGainDb}|{_settings.Bass.ShelfQ}";

    private void UpdatePresetDetection()
    {
        _suppressPreset = true;
        try
        {
            var detected = BassPresets.DetectPreset(_settings.Bass);
            _settings.Bass.PresetName = detected;
        }
        finally
        {
            _suppressPreset = false;
        }
    }

    private void UpdateResponse()
    {
        Response = CurveSampler.Bass(_settings.Bass);
        OnPropertyChanged(nameof(EffectiveGainDescription));
    }

    private void ResetToDefaults()
    {
        _suppressPreset = true;
        try
        {
            _settings.Bass.Intensity = 45;
            _settings.Bass.FrequencyHz = 80;
            _settings.Bass.MaxGainDb = 18;
            _settings.Bass.ShelfQ = 0.707;
            _settings.Bass.PresetName = BassPresets.Custom;
        }
        finally
        {
            _suppressPreset = false;
        }

        OnPropertyChanged(nameof(Intensity), nameof(FrequencyHz), nameof(MaxGainDb), nameof(ShelfQ),
            nameof(SelectedPreset), nameof(EffectiveGainDb), nameof(GainText), nameof(FrequencyText),
            nameof(EffectiveGainDescription), nameof(QText), nameof(Description));
        UpdateResponse();
        _changed();
    }

    public void ReloadFromSettings()
    {
        OnPropertyChanged(nameof(Enabled), nameof(Intensity), nameof(FrequencyHz), nameof(MaxGainDb),
            nameof(ShelfQ), nameof(SelectedPreset), nameof(EffectiveGainDb), nameof(GainText),
            nameof(FrequencyText), nameof(EffectiveGainDescription), nameof(QText), nameof(Description),
            nameof(StatusText), nameof(ActiveLabel));
        UpdateResponse();
    }
}
