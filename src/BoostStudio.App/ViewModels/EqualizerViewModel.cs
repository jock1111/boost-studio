using System.Collections.ObjectModel;
using BoostStudio.App.Infrastructure;
using BoostStudio.App.Services;
using BoostStudio.Core.Models;
using BoostStudio.Core.Storage;

namespace BoostStudio.App.ViewModels;

public sealed record EqPresetChoice(
    string Name,
    string DisplayName,
    string Description,
    double[] GainsDb,
    bool IsUser,
    bool IsCustom = false)
{
    public static EqPresetChoice FromBuiltIn(EqBuiltInPreset preset) => new(
        preset.Name, preset.Name, preset.Description, (double[])preset.GainsDb.Clone(), false);

    public static EqPresetChoice FromUser(UserEqPreset preset) => new(
        preset.Name, $"{preset.Name}  ·  User", "Your saved equalizer curve",
        (double[])preset.GainsDb.Clone(), true);
}

public sealed class EqBandViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly Action<int> _changed;
    private readonly int _index;

    public EqBandViewModel(AppSettings settings, int index, Action<int> changed)
    {
        _settings = settings;
        _index = index;
        _changed = changed;
    }

    public int Index => _index;
    public string Label => EqConstants.BandLabels[_index];
    public double FrequencyHz => EqConstants.BandFrequencies[_index];
    public string FrequencyText => _index == 0 ? "31 Hz" : $"{Label} Hz";

    public double Value
    {
        get => _settings.Equalizer.GainsDb[_index];
        set
        {
            var clamped = Math.Round(Math.Clamp(value, -12, 12), 1);
            if (Math.Abs(_settings.Equalizer.GainsDb[_index] - clamped) < 0.001) return;
            _settings.Equalizer.GainsDb[_index] = clamped;
            OnPropertyChanged(nameof(Value), nameof(ValueText));
            _changed(_index);
        }
    }

    public string ValueText
    {
        get
        {
            var value = Value;
            return Math.Abs(value) < 0.05 ? "0.0" : value.ToString("+0.0;-0.0");
        }
    }

    public void SetSilently(double value)
    {
        var clamped = Math.Round(Math.Clamp(value, -12, 12), 1);
        if (Math.Abs(_settings.Equalizer.GainsDb[_index] - clamped) < 0.001) return;
        _settings.Equalizer.GainsDb[_index] = clamped;
        OnPropertyChanged(nameof(Value), nameof(ValueText));
    }
}

public sealed class EqualizerViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly PresetStore _presetStore;
    private readonly IDialogService _dialogs;
    private readonly Action _changed;
    private UserPresetLibrary _library;
    private bool _suppressSelection;
    private EqPresetChoice? _selectedChoice;
    private double[] _response;
    private string _statusText = "Ten ISO octave bands, applied as peaking filters.";
    private bool _canRename;
    private bool _canDelete;

    public EqualizerViewModel(AppSettings settings, UserPresetLibrary library, PresetStore presetStore,
        IDialogService dialogs, Action changed)
    {
        _settings = settings;
        _library = library;
        _presetStore = presetStore;
        _dialogs = dialogs;
        _changed = changed;

        Bands = new ObservableCollection<EqBandViewModel>(
            Enumerable.Range(0, EqConstants.BandCount)
                .Select(i => new EqBandViewModel(settings, i, OnBandChanged)));
        SavePresetCommand = new RelayCommand(SavePreset);
        RenamePresetCommand = new RelayCommand(RenamePreset, () => _canRename);
        DeletePresetCommand = new RelayCommand(DeletePreset, () => _canDelete);
        FlatCommand = new RelayCommand(Flatten);
        PresetChoices = new ObservableCollection<EqPresetChoice>();
        BuildChoices(_settings.Equalizer.SelectedPresetName);
        _response = CurveSampler.Equalizer(settings.Equalizer.GainsDb);
        RefreshCanRename();
    }

    public ObservableCollection<EqBandViewModel> Bands { get; }
    public ObservableCollection<EqPresetChoice> PresetChoices { get; }
    public RelayCommand SavePresetCommand { get; }
    public RelayCommand RenamePresetCommand { get; }
    public RelayCommand DeletePresetCommand { get; }
    public RelayCommand FlatCommand { get; }

    public bool Enabled
    {
        get => _settings.Equalizer.Enabled;
        set
        {
            if (_settings.Equalizer.Enabled == value) return;
            _settings.Equalizer.Enabled = value;
            OnPropertyChanged(nameof(Enabled), nameof(StatusText), nameof(ActiveLabel), nameof(Description));
            _changed();
        }
    }

    public string StatusText => Enabled ? "Equalizer is active" : "Equalizer is off";
    public string ActiveLabel => Enabled ? "ACTIVE" : "OFF";
    public string Description => Enabled
        ? $"{_settings.Equalizer.SelectedPresetName} curve · {ActiveBandCount} active bands"
        : "Equalizer is bypassed. Your curve is retained.";

    public int ActiveBandCount => _settings.Equalizer.GainsDb.Count(g => Math.Abs(g) >= 0.05);
    public string BandSummary => ActiveBandCount == 1 ? "1 band shaped" : $"{ActiveBandCount} bands shaped";
    public string UserPresetCountText => _library.EqualizerPresets.Count == 1
        ? "1 user preset"
        : $"{_library.EqualizerPresets.Count} user presets";

    public EqPresetChoice? SelectedPresetChoice
    {
        get => _selectedChoice;
        set
        {
            if (value is null || _suppressSelection) return;
            _selectedChoice = value;
            ApplyChoice(value);
        }
    }

    public string SelectedPresetName => _settings.Equalizer.SelectedPresetName;
    public string PresetDescription => SelectedPresetChoice?.Description ?? "Hand-tuned equalizer curve";

    public double[] Response
    {
        get => _response;
        private set => SetProperty(ref _response, value);
    }

    public string StatusMessage
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    private void ApplyChoice(EqPresetChoice choice)
    {
        _suppressSelection = true;
        try
        {
            if (!choice.IsCustom)
            {
                for (var i = 0; i < Bands.Count; i++)
                    Bands[i].SetSilently(i < choice.GainsDb.Length ? choice.GainsDb[i] : 0);
            }

            _settings.Equalizer.SelectedPresetName = choice.Name;
        }
        finally
        {
            _suppressSelection = false;
        }

        RefreshCanRename();
        OnPropertyChanged(nameof(SelectedPresetChoice), nameof(SelectedPresetName), nameof(PresetDescription),
            nameof(Description), nameof(BandSummary), nameof(ActiveBandCount));
        UpdateResponse();
        StatusMessage = choice.IsCustom
            ? "Custom curve. Save it as a user preset to reuse it."
            : choice.IsUser
                ? $"Loaded user preset “{choice.Name}”."
                : $"Loaded built-in preset “{choice.Name}”.";
        _changed();
    }

    private void OnBandChanged(int index)
    {
        if (_suppressSelection) return;

        var detected = EqPresets.Detect(_settings.Equalizer.GainsDb);
        var selectedName = detected;
        if (detected == EqPresets.Custom && _selectedChoice is { IsUser: true } current)
        {
            var stillMatches = current.GainsDb.Length == _settings.Equalizer.GainsDb.Length &&
                               current.GainsDb.Select((g, i) => Math.Abs(g - _settings.Equalizer.GainsDb[i]) < 0.05).All(x => x);
            if (stillMatches) selectedName = current.Name;
        }

        _settings.Equalizer.SelectedPresetName = selectedName;
        _selectedChoice = PresetChoices.FirstOrDefault(c =>
            string.Equals(c.Name, selectedName, StringComparison.OrdinalIgnoreCase) &&
            !c.IsCustom) ?? PresetChoices.FirstOrDefault(c => c.IsCustom);
        RefreshCanRename();
        OnPropertyChanged(nameof(SelectedPresetChoice), nameof(SelectedPresetName), nameof(PresetDescription),
            nameof(Description), nameof(BandSummary), nameof(ActiveBandCount));
        UpdateResponse();
        StatusMessage = "Custom curve. Save it as a user preset to reuse it.";
        _changed();
    }

    private void UpdateResponse()
    {
        Response = CurveSampler.Equalizer(_settings.Equalizer.GainsDb);
    }

    private void BuildChoices(string? selectedName)
    {
        var keep = selectedName ?? _settings.Equalizer.SelectedPresetName;
        _suppressSelection = true;
        try
        {
            PresetChoices.Clear();
            foreach (var preset in EqPresets.All)
                PresetChoices.Add(EqPresetChoice.FromBuiltIn(preset));
            PresetChoices.Add(new EqPresetChoice(EqPresets.Custom, "Custom",
                "Hand-tuned response, not tied to a preset", (double[])_settings.Equalizer.GainsDb.Clone(),
                false, true));
            foreach (var preset in _library.EqualizerPresets.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
                PresetChoices.Add(EqPresetChoice.FromUser(preset));

            var selected = PresetChoices.FirstOrDefault(c =>
                    !c.IsCustom && !c.IsUser && string.Equals(c.Name, keep, StringComparison.OrdinalIgnoreCase))
                ?? PresetChoices.FirstOrDefault(c =>
                    c.IsUser && string.Equals(c.Name, keep, StringComparison.OrdinalIgnoreCase))
                ?? PresetChoices.First(c => c.IsCustom);

            _selectedChoice = selected;
            _settings.Equalizer.SelectedPresetName = selected.IsCustom ? EqPresets.Custom : selected.Name;
        }
        finally
        {
            _suppressSelection = false;
        }

        RefreshCanRename();
        OnPropertyChanged(nameof(SelectedPresetChoice), nameof(SelectedPresetName), nameof(PresetDescription),
            nameof(UserPresetCountText));
    }

    private void RefreshCanRename()
    {
        _canRename = _selectedChoice is { IsUser: true };
        _canDelete = _canRename;
        RenamePresetCommand.RaiseCanExecuteChanged();
        DeletePresetCommand.RaiseCanExecuteChanged();
    }

    private void SavePreset()
    {
        var suggested = SelectedPresetChoice is { IsUser: true } user ? user.Name : "My curve";
        var name = _dialogs.Prompt("Save equalizer preset", "Save the current ten-band curve as a reusable preset.",
            "Preset name", suggested, "Names are stored locally and can be renamed later.", 40);
        if (name is null) return;

        name = name.Trim();
        if (name.Length == 0) return;
        if (EqPresets.All.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            _dialogs.ShowWarning("Reserved preset name", "That name is used by a built-in preset. Choose another name.");
            return;
        }

        var existing = _library.EqualizerPresets.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null &&
            !_dialogs.Confirm("Replace preset?", $"A user preset named “{name}” already exists. Replace its curve?",
                "Replace", "Cancel", danger: true))
            return;

        if (existing is null)
        {
            existing = new UserEqPreset { Name = name, CreatedUtc = DateTime.UtcNow };
            _library.EqualizerPresets.Add(existing);
        }

        existing.GainsDb = (double[])_settings.Equalizer.GainsDb.Clone();
        existing.ModifiedUtc = DateTime.UtcNow;
        if (!TrySaveLibrary()) return;

        _settings.Equalizer.SelectedPresetName = name;
        BuildChoices(name);
        UpdateResponse();
        StatusMessage = $"Saved preset “{name}”.";
        OnPropertyChanged(nameof(Description), nameof(ActiveBandCount), nameof(BandSummary));
        _changed();
    }

    private void RenamePreset()
    {
        if (SelectedPresetChoice is not { IsUser: true } selected)
        {
            _dialogs.ShowInfo("Built-in preset", "Built-in presets cannot be renamed. Save a user preset first.");
            return;
        }

        var name = _dialogs.Prompt("Rename equalizer preset", "Choose a new name for this user preset.",
            "Preset name", selected.Name, "Built-in preset names are reserved.", 40);
        if (name is null) return;
        name = name.Trim();
        if (name.Length == 0) return;
        if (EqPresets.All.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            _dialogs.ShowWarning("Reserved preset name", "That name is used by a built-in preset. Choose another name.");
            return;
        }

        if (_library.EqualizerPresets.Any(p => !string.Equals(p.Name, selected.Name, StringComparison.OrdinalIgnoreCase) &&
                                                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            _dialogs.ShowWarning("Name already in use", "Another user preset already has that name.");
            return;
        }

        var preset = _library.EqualizerPresets.FirstOrDefault(p =>
            string.Equals(p.Name, selected.Name, StringComparison.OrdinalIgnoreCase));
        if (preset is null) return;
        preset.Name = name;
        preset.ModifiedUtc = DateTime.UtcNow;
        if (!TrySaveLibrary()) return;

        _settings.Equalizer.SelectedPresetName = name;
        BuildChoices(name);
        StatusMessage = $"Renamed preset to “{name}”.";
        _changed();
    }

    private void DeletePreset()
    {
        if (SelectedPresetChoice is not { IsUser: true } selected)
        {
            _dialogs.ShowInfo("Built-in preset", "Built-in presets cannot be deleted.");
            return;
        }

        if (!_dialogs.Confirm("Delete equalizer preset?",
                $"Delete the user preset “{selected.Name}”? The current curve stays loaded as a custom curve.",
                "Delete", "Cancel", danger: true))
            return;

        var preset = _library.EqualizerPresets.FirstOrDefault(p =>
            string.Equals(p.Name, selected.Name, StringComparison.OrdinalIgnoreCase));
        if (preset is null) return;
        _library.EqualizerPresets.Remove(preset);
        if (!TrySaveLibrary()) return;

        _settings.Equalizer.SelectedPresetName = EqPresets.Custom;
        BuildChoices(EqPresets.Custom);
        StatusMessage = $"Deleted preset “{selected.Name}”.";
        OnPropertyChanged(nameof(Description));
        _changed();
    }

    private bool TrySaveLibrary()
    {
        try
        {
            _presetStore.Save(_library);
            OnPropertyChanged(nameof(UserPresetCountText));
            return true;
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Could not save presets", "The preset library could not be written.", ex.Message);
            return false;
        }
    }

    private void Flatten()
    {
        _suppressSelection = true;
        try
        {
            for (var i = 0; i < Bands.Count; i++)
                Bands[i].SetSilently(0);
            _settings.Equalizer.SelectedPresetName = "Balanced";
        }
        finally
        {
            _suppressSelection = false;
        }

        _selectedChoice = PresetChoices.FirstOrDefault(c => c.Name == "Balanced");
        RefreshCanRename();
        OnPropertyChanged(nameof(SelectedPresetChoice), nameof(SelectedPresetName), nameof(PresetDescription),
            nameof(Description), nameof(ActiveBandCount), nameof(BandSummary));
        UpdateResponse();
        StatusMessage = "All bands reset to 0 dB.";
        _changed();
    }

    public void ReplaceLibrary(UserPresetLibrary library)
    {
        _library = library;
        BuildChoices(_settings.Equalizer.SelectedPresetName);
        OnPropertyChanged(nameof(UserPresetCountText));
    }

    public void ReloadFromSettings()
    {
        foreach (var band in Bands)
            band.SetSilently(_settings.Equalizer.GainsDb[band.Index]);

        BuildChoices(_settings.Equalizer.SelectedPresetName);
        UpdateResponse();
        OnPropertyChanged(nameof(Enabled), nameof(StatusText), nameof(ActiveLabel), nameof(Description),
            nameof(ActiveBandCount), nameof(BandSummary), nameof(SelectedPresetName));
        StatusMessage = "Settings reloaded.";
    }
}
