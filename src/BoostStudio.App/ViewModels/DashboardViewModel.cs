using System.ComponentModel;
using BoostStudio.App.Infrastructure;
using BoostStudio.Core.Models;

namespace BoostStudio.App.ViewModels;

public sealed class DashboardViewModel : ObservableObject
{
    private readonly Func<bool> _getMaster;
    private readonly Action<bool> _setMaster;

    public DashboardViewModel(
        BassBoosterViewModel bass,
        VolumeBoosterViewModel volume,
        EqualizerViewModel equalizer,
        MonitorViewModel monitor,
        TestToneViewModel testTone,
        EngineStatusViewModel engine,
        Func<bool> getMaster,
        Action<bool> setMaster,
        Action<string> navigate)
    {
        Bass = bass;
        Volume = volume;
        Equalizer = equalizer;
        Monitor = monitor;
        TestTone = testTone;
        Engine = engine;
        _getMaster = getMaster;
        _setMaster = setMaster;
        NavigateCommand = new RelayCommand<string>(key =>
        {
            if (!string.IsNullOrWhiteSpace(key))
                navigate(key);
        });

        Bass.PropertyChanged += OnChildChanged;
        Volume.PropertyChanged += OnChildChanged;
        Equalizer.PropertyChanged += OnChildChanged;
        Engine.PropertyChanged += OnChildChanged;
    }

    public BassBoosterViewModel Bass { get; }
    public VolumeBoosterViewModel Volume { get; }
    public EqualizerViewModel Equalizer { get; }
    public MonitorViewModel Monitor { get; }
    public TestToneViewModel TestTone { get; }
    public EngineStatusViewModel Engine { get; }
    public RelayCommand<string> NavigateCommand { get; }

    public bool ProcessingEnabled
    {
        get => _getMaster();
        set
        {
            if (_getMaster() == value) return;
            _setMaster(value);
            Refresh();
        }
    }

    public string Greeting => DateTime.Now.Hour switch
    {
        < 5 => "Good night",
        < 12 => "Good morning",
        < 18 => "Good afternoon",
        _ => "Good evening"
    };

    public string HeroTitle => "Shape the sound. Feel the difference.";
    public string HeroSubtitle => "System-wide bass, gain and ten-band EQ through Equalizer APO.";

    public int ActiveModuleCount =>
        (Bass.Enabled ? 1 : 0) + (Volume.Enabled ? 1 : 0) + (Equalizer.Enabled ? 1 : 0);

    public string ActiveModules => !ProcessingEnabled
        ? "Master bypass"
        : ActiveModuleCount switch
        {
            0 => "Standby",
            1 => Bass.Enabled ? "Bass Booster" : Volume.Enabled ? "Volume Booster" : "Equalizer",
            2 => Bass.Enabled && Volume.Enabled
                ? "Bass + Volume"
                : Bass.Enabled ? "Bass + EQ" : "Volume + EQ",
            _ => "Bass + Volume + EQ"
        };

    public string HeroStatus => !ProcessingEnabled
        ? "Processing is bypassed. Your modules and settings are untouched."
        : ActiveModuleCount == 0
            ? "No modules are enabled yet. Turn on a module to start shaping audio."
            : $"{ActiveModules} applying in real time through Equalizer APO.";

    public string MasterStatus => ProcessingEnabled ? "MASTER ON" : "MASTER BYPASSED";
    public string ActiveCountText => ActiveModuleCount == 1 ? "1 module active" : $"{ActiveModuleCount} modules active";
    public string FilterCountText => Engine.FilterSummary;
    public string HeadroomText => Engine.HeadroomText;
    public string EngineStatusText => Engine.StateLabel;
    public string MonitorStatusText => Monitor.IsActive ? Monitor.StatusText : "Audio monitoring is offline";
    public string BassPresetText => Bass.SelectedPreset?.Name ?? "Custom";
    public string EqPresetText => Equalizer.SelectedPresetName;
    public string VolumePresetText => Volume.AutoGainControl ? "Auto gain on" : "Manual gain";

    public void Refresh()
    {
        OnPropertyChanged(nameof(ProcessingEnabled), nameof(ActiveModuleCount), nameof(ActiveModules),
            nameof(HeroStatus), nameof(MasterStatus), nameof(ActiveCountText), nameof(FilterCountText),
            nameof(HeadroomText), nameof(EngineStatusText), nameof(MonitorStatusText),
            nameof(BassPresetText), nameof(EqPresetText), nameof(VolumePresetText));
    }

    private void OnChildChanged(object? sender, PropertyChangedEventArgs e) => Refresh();
}
