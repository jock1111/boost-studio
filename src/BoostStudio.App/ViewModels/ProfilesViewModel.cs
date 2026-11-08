using System.Collections.ObjectModel;
using BoostStudio.App.Infrastructure;
using BoostStudio.App.Services;
using BoostStudio.Core.Models;
using BoostStudio.Core.Storage;

namespace BoostStudio.App.ViewModels;

/// <summary>One row in the profile list with live active/modified state.</summary>
public sealed class ProfileItemViewModel : ObservableObject
{
    private bool _isActive;
    private bool _isModified;

    public ProfileItemViewModel(SoundProfile profile)
    {
        Profile = profile;
    }

    public SoundProfile Profile { get; }

    public string Name => Profile.Name;
    public string Summary => Profile.ModuleSummary;
    public string Detail => Profile.DetailSummary;
    public string UpdatedText => $"Updated {Profile.ModifiedUtc.ToLocalTime():MMM d, yyyy · HH:mm}";

    public bool IsActive
    {
        get => _isActive;
        private set => SetProperty(ref _isActive, value);
    }

    public bool IsModified
    {
        get => _isModified;
        private set => SetProperty(ref _isModified, value);
    }

    public string StatusText => !IsActive
        ? "Not applied"
        : IsModified ? "Applied · modified" : "Applied";

    public void Refresh(SoundProfile? active, AppSettings settings)
    {
        var activeNow = active is not null &&
                        string.Equals(active.Id, Profile.Id, StringComparison.OrdinalIgnoreCase);
        var modified = activeNow && !Profile.Matches(settings);

        if (_isActive != activeNow)
        {
            _isActive = activeNow;
            OnPropertyChanged(nameof(IsActive), nameof(StatusText));
        }

        if (_isModified != modified)
        {
            _isModified = modified;
            OnPropertyChanged(nameof(IsModified), nameof(StatusText));
        }
    }

    public void NotifyMetadataChanged() =>
        OnPropertyChanged(nameof(Name), nameof(Summary), nameof(Detail), nameof(UpdatedText),
            nameof(StatusText));
}

/// <summary>
/// Named complete sound setups. Applying a profile changes only the processing
/// state (master, Bass, Volume, EQ and auto headroom); application preferences,
/// hotkeys and routing are untouched. The last apply can be reverted from here.
/// </summary>
public sealed class ProfilesViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly ProfileStore _store;
    private readonly IDialogService _dialogs;
    private readonly Action _reloadAll;
    private readonly Action<string> _navigate;
    private ProfileLibrary _library;
    private ProfileItemViewModel? _selectedProfile;
    private SoundProfile? _previousAudioState;
    private string? _previousActiveProfileId;
    private string _statusMessage = "Profiles capture Bass, Volume, EQ and the master switch. App preferences stay untouched.";

    public ProfilesViewModel(
        AppSettings settings,
        ProfileLibrary library,
        ProfileStore store,
        IDialogService dialogs,
        Action reloadAll,
        Action<string> navigate)
    {
        _settings = settings;
        _library = library;
        _store = store;
        _dialogs = dialogs;
        _reloadAll = reloadAll;
        _navigate = navigate;

        Profiles = new ObservableCollection<ProfileItemViewModel>();
        NavigateCommand = new RelayCommand<string>(key =>
        {
            if (!string.IsNullOrWhiteSpace(key)) _navigate(key);
        });
        SaveCurrentCommand = new RelayCommand(SaveCurrent);
        ApplyCommand = new RelayCommand(ApplySelected, () => SelectedProfile is not null);
        UpdateCommand = new RelayCommand(UpdateSelected, () => SelectedProfile is not null);
        RenameCommand = new RelayCommand(RenameSelected, () => SelectedProfile is not null);
        DeleteCommand = new RelayCommand(DeleteSelected, () => SelectedProfile is not null);
        UndoCommand = new RelayCommand(UndoApply, () => _previousAudioState is not null);

        RebuildItems();
    }

    public ObservableCollection<ProfileItemViewModel> Profiles { get; }
    public RelayCommand SaveCurrentCommand { get; }
    public RelayCommand ApplyCommand { get; }
    public RelayCommand UpdateCommand { get; }
    public RelayCommand RenameCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand<string> NavigateCommand { get; }

    /// <summary>Raised when the library or active profile changes, for the tray menu.</summary>
    public event EventHandler? LibraryChanged;

    public ProfileLibrary Library => _library;
    public IReadOnlyList<SoundProfile> ProfileModels => _library.Profiles;
    public string? ActiveProfileId => _library.ActiveProfileId;

    public ProfileItemViewModel? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!SetProperty(ref _selectedProfile, value)) return;
            OnPropertyChanged(nameof(SelectedProfileDetail));
            RaiseSelectionCommands();
        }
    }

    public string SelectedProfileDetail => SelectedProfile?.Profile.DetailSummary
        ?? "Select a profile to apply, update, rename or delete it.";

    public bool HasProfiles => Profiles.Count > 0;
    public bool CanUndo => _previousAudioState is not null;

    public string CurrentSetupSummary
    {
        get
        {
            if (!_settings.ProcessingEnabled) return "Master bypassed";
            if (!_settings.Bass.Enabled && !_settings.Volume.Enabled && !_settings.Equalizer.Enabled)
                return "Standby · no modules enabled";

            var parts = new List<string>();
            if (_settings.Bass.Enabled)
                parts.Add($"Bass {_settings.Bass.EffectiveGainDb:+0.0;-0.0;0.0} dB");
            if (_settings.Volume.Enabled)
                parts.Add($"Volume {_settings.Volume.EffectiveBoostDb:+0.0;-0.0;0.0} dB");
            if (_settings.Equalizer.Enabled)
                parts.Add($"EQ {_settings.Equalizer.SelectedPresetName}");
            return string.Join("  ·  ", parts);
        }
    }

    public string CurrentSetupDetail
    {
        get
        {
            var modules = (_settings.Bass.Enabled ? 1 : 0) +
                          (_settings.Volume.Enabled ? 1 : 0) +
                          (_settings.Equalizer.Enabled ? 1 : 0);
            var headroom = _settings.AutoHeadroom ? "auto headroom on" : "auto headroom off";
            return $"{modules} of 3 modules enabled · {headroom}";
        }
    }

    public string ActiveProfileText
    {
        get
        {
            var active = _library.Find(_library.ActiveProfileId);
            if (active is null) return "No profile applied";
            return active.Matches(_settings)
                ? $"Applied: {active.Name}"
                : $"Applied: {active.Name} · modified";
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public void RefreshFromSettings()
    {
        var active = _library.Find(_library.ActiveProfileId);
        foreach (var item in Profiles)
            item.Refresh(active, _settings);

        OnPropertyChanged(nameof(CurrentSetupSummary), nameof(CurrentSetupDetail),
            nameof(ActiveProfileText), nameof(CanUndo));
        UndoCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Replaces the entire profile library, e.g. after a backup import.</summary>
    public void ReplaceLibrary(ProfileLibrary library)
    {
        _library = library;
        _library.Normalize();
        _previousAudioState = null;
        _previousActiveProfileId = null;
        RebuildItems();
        StatusMessage = _library.Profiles.Count == 0
            ? "No profiles yet. Save the current setup to create one."
            : "Profiles imported.";
        RaiseLibraryChanged();
    }

    public void ApplyById(string profileId)
    {
        var item = Profiles.FirstOrDefault(p =>
            string.Equals(p.Profile.Id, profileId, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;
        SelectedProfile = item;
        ApplySelected();
    }

    private void SaveCurrent()
    {
        var suggested = _library.Profiles.Count == 0 ? "My setup" : "New profile";
        var name = _dialogs.Prompt("Save sound profile",
            "Save the current Bass, Volume, EQ and master state as a reusable setup.",
            "Profile name", suggested, "Profiles are local and are included in Boost Studio backups.", SoundProfile.MaxNameLength);
        if (name is null) return;

        name = SoundProfile.NormalizeName(name);
        var existing = _library.FindByName(name);
        if (existing is not null)
        {
            if (!_dialogs.Confirm("Replace existing profile?",
                    $"A profile named “{existing.Name}” already exists. Replace it with the current setup?",
                    "Replace profile", "Cancel", danger: true))
                return;

            var index = _library.Profiles.IndexOf(existing);
            var previousActiveId = _library.ActiveProfileId;
            var replacement = SoundProfile.Capture(name, _settings);
            replacement.Id = existing.Id;
            replacement.CreatedUtc = existing.CreatedUtc;
            _library.Profiles[index] = replacement;
            _library.ActiveProfileId = replacement.Id;

            if (!TrySaveLibrary())
            {
                _library.Profiles[index] = existing;
                _library.ActiveProfileId = previousActiveId;
                return;
            }

            RebuildItems(replacement.Id);
            StatusMessage = $"Updated “{replacement.Name}” with the current setup.";
            RefreshFromSettings();
            RaiseLibraryChanged();
            return;
        }

        if (_library.Profiles.Count >= 50)
        {
            _dialogs.ShowWarning("Profile limit reached",
                "Boost Studio supports up to 50 sound profiles. Delete one you no longer need first.");
            return;
        }

        var previousActive = _library.ActiveProfileId;
        var profile = SoundProfile.Capture(name, _settings);
        _library.Profiles.Add(profile);
        _library.ActiveProfileId = profile.Id;

        if (!TrySaveLibrary())
        {
            _library.Profiles.Remove(profile);
            _library.ActiveProfileId = previousActive;
            RebuildItems();
            return;
        }

        RebuildItems(profile.Id);
        StatusMessage = $"Saved “{profile.Name}”. The current setup is now this profile.";
        RefreshFromSettings();
        RaiseLibraryChanged();
    }

    private void ApplySelected()
    {
        var item = SelectedProfile;
        if (item is null) return;

        _previousAudioState = SoundProfile.Capture("Previous setup", _settings);
        _previousActiveProfileId = _library.ActiveProfileId;
        item.Profile.ApplyTo(_settings);

        _library.ActiveProfileId = item.Profile.Id;
        var saved = TrySaveLibrary();
        _reloadAll();
        RefreshFromSettings();

        StatusMessage = saved
            ? $"Applied “{item.Name}”. Use Undo to return to the previous setup."
            : $"Applied “{item.Name}”, but the profile file could not be saved.";
        UndoCommand.RaiseCanExecuteChanged();
        RaiseLibraryChanged();
    }

    private void UndoApply()
    {
        if (_previousAudioState is null) return;

        _previousAudioState.ApplyTo(_settings);
        _library.ActiveProfileId = _previousActiveProfileId;
        _previousAudioState = null;
        _previousActiveProfileId = null;
        TrySaveLibrary();
        _reloadAll();
        RefreshFromSettings();
        StatusMessage = "Restored the setup from before the last profile change.";
        UndoCommand.RaiseCanExecuteChanged();
        RaiseLibraryChanged();
    }

    private void UpdateSelected()
    {
        var item = SelectedProfile;
        if (item is null) return;

        if (!_dialogs.Confirm("Update profile?",
                $"Replace “{item.Name}” with the current Bass, Volume, EQ and master state?",
                "Update profile", "Cancel", danger: true))
            return;

        var index = _library.Profiles.IndexOf(item.Profile);
        var original = item.Profile.Clone();
        var previousActiveId = _library.ActiveProfileId;
        item.Profile.UpdateFrom(_settings);
        _library.ActiveProfileId = item.Profile.Id;
        if (!TrySaveLibrary())
        {
            // Roll the in-memory profile back as well: leaving it newer than the
            // file would make a later save silently persist an unconfirmed update.
            if (index >= 0)
                _library.Profiles[index] = original;
            _library.ActiveProfileId = previousActiveId;
            RebuildItems(original.Id);
            StatusMessage = "The profile could not be saved. Check the log for details.";
            RefreshFromSettings();
            return;
        }

        item.NotifyMetadataChanged();
        RebuildItems(item.Profile.Id);
        StatusMessage = $"Updated “{item.Name}” from the current setup.";
        RefreshFromSettings();
        RaiseLibraryChanged();
    }

    private void RenameSelected()
    {
        var item = SelectedProfile;
        if (item is null) return;

        var name = _dialogs.Prompt("Rename sound profile", "Choose a new name for this profile.",
            "Profile name", item.Name, "Names must be unique.", SoundProfile.MaxNameLength);
        if (name is null) return;

        name = SoundProfile.NormalizeName(name);
        var duplicate = _library.FindByName(name);
        if (duplicate is not null && !ReferenceEquals(duplicate, item.Profile))
        {
            _dialogs.ShowWarning("Name already in use", "Another sound profile already has that name.");
            return;
        }

        var oldName = item.Profile.Name;
        var oldModified = item.Profile.ModifiedUtc;
        item.Profile.Name = name;
        item.Profile.ModifiedUtc = DateTime.UtcNow;
        if (!TrySaveLibrary())
        {
            item.Profile.Name = oldName;
            item.Profile.ModifiedUtc = oldModified;
            return;
        }

        RebuildItems(item.Profile.Id);
        StatusMessage = $"Renamed the profile to “{name}”.";
        RefreshFromSettings();
        RaiseLibraryChanged();
    }

    private void DeleteSelected()
    {
        var item = SelectedProfile;
        if (item is null) return;

        if (!_dialogs.Confirm("Delete sound profile?",
                $"Delete “{item.Name}”? This removes the saved setup only; the live audio settings stay as they are.",
                "Delete profile", "Cancel", danger: true))
            return;

        var index = _library.Profiles.IndexOf(item.Profile);
        var wasActive = string.Equals(_library.ActiveProfileId, item.Profile.Id, StringComparison.OrdinalIgnoreCase);
        _library.Profiles.Remove(item.Profile);
        if (wasActive)
            _library.ActiveProfileId = null;

        if (!TrySaveLibrary())
        {
            if (index >= 0 && index <= _library.Profiles.Count)
                _library.Profiles.Insert(index, item.Profile);
            if (wasActive)
                _library.ActiveProfileId = item.Profile.Id;
            return;
        }

        RebuildItems(_library.Profiles.Count == 0
            ? null
            : _library.Profiles[Math.Clamp(index, 0, _library.Profiles.Count - 1)].Id);
        StatusMessage = $"Deleted “{item.Name}”. The live audio settings were not changed.";
        RefreshFromSettings();
        RaiseLibraryChanged();
    }

    private void RebuildItems(string? selectId = null)
    {
        var previousId = selectId ?? _selectedProfile?.Profile.Id;
        var active = _library.Find(_library.ActiveProfileId);

        Profiles.Clear();
        foreach (var profile in _library.Profiles)
            Profiles.Add(new ProfileItemViewModel(profile));

        _selectedProfile = Profiles.FirstOrDefault(p =>
                                string.Equals(p.Profile.Id, previousId, StringComparison.OrdinalIgnoreCase))
                            ?? Profiles.FirstOrDefault();

        foreach (var item in Profiles)
            item.Refresh(active, _settings);

        OnPropertyChanged(nameof(SelectedProfile), nameof(SelectedProfileDetail), nameof(HasProfiles),
            nameof(CurrentSetupSummary), nameof(CurrentSetupDetail), nameof(ActiveProfileText),
            nameof(CanUndo));
        RaiseSelectionCommands();
        UndoCommand.RaiseCanExecuteChanged();
    }

    private void RaiseSelectionCommands()
    {
        ApplyCommand.RaiseCanExecuteChanged();
        UpdateCommand.RaiseCanExecuteChanged();
        RenameCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
    }

    private bool TrySaveLibrary()
    {
        try
        {
            _store.Save(_library);
            return true;
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Could not save sound profiles",
                "The profile file could not be written. Your live audio settings are unaffected.", ex.Message);
            return false;
        }
    }

    private void RaiseLibraryChanged() => LibraryChanged?.Invoke(this, EventArgs.Empty);
}
