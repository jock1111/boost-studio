using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Input;
using BoostStudio.App.Infrastructure;
using BoostStudio.App.Services;
using BoostStudio.Core.Audio;
using BoostStudio.Core.Engine;
using BoostStudio.Core.Models;
using BoostStudio.Core.Storage;

namespace BoostStudio.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly IDialogService _dialogs;
    private readonly ILogService _log;
    private readonly ShellService _shell;
    private readonly StartupService _startup;
    private readonly AudioDeviceService _audioDevices;
    private readonly BackupService _backup;
    private readonly AudioEngineService _engine;
    private readonly MonitorViewModel _monitor;
    private readonly Action _settingsChanged;
    private readonly Action _audioRoutingChanged;
    private readonly Action _hotkeysChanged;
    private readonly Action _reloadAll;
    private readonly Action<UserPresetLibrary> _libraryReplaced;
    private readonly Func<UserPresetLibrary> _libraryProvider;
    private readonly Func<ProfileLibrary> _profileLibraryProvider;
    private readonly Action<ProfileLibrary> _profileLibraryReplaced;

    private ObservableCollection<AudioDeviceInfo> _devices = new();
    private AudioDeviceInfo? _selectedMonitoringDevice;
    private AudioDeviceInfo? _selectedProcessingDevice;
    private bool _suppressMonitoringEvents;
    private string _hotkeyStatus = "Global shortcuts are validated when applied.";
    private bool _hotkeyStatusIsError;
    private string _lastBackupText = "No backup created in this session";

    public SettingsViewModel(
        AppSettings settings,
        IDialogService dialogs,
        ILogService log,
        ShellService shell,
        StartupService startup,
        AudioDeviceService audioDevices,
        BackupService backup,
        AudioEngineService engine,
        MonitorViewModel monitor,
        Action settingsChanged,
        Action audioRoutingChanged,
        Action hotkeysChanged,
        Action reloadAll,
        Action<UserPresetLibrary> libraryReplaced,
        Func<UserPresetLibrary> libraryProvider,
        Func<ProfileLibrary> profileLibraryProvider,
        Action<ProfileLibrary> profileLibraryReplaced)
    {
        _settings = settings;
        _dialogs = dialogs;
        _log = log;
        _shell = shell;
        _startup = startup;
        _audioDevices = audioDevices;
        _backup = backup;
        _engine = engine;
        _monitor = monitor;
        _settingsChanged = settingsChanged;
        _audioRoutingChanged = audioRoutingChanged;
        _hotkeysChanged = hotkeysChanged;
        _reloadAll = reloadAll;
        _libraryReplaced = libraryReplaced;
        _libraryProvider = libraryProvider;
        _profileLibraryProvider = profileLibraryProvider;
        _profileLibraryReplaced = profileLibraryReplaced;

        RefreshDevicesCommand = new RelayCommand(() => RefreshDevices(true));
        SetWindowsDefaultCommand = new RelayCommand(SetWindowsDefault);
        OpenSoundSettingsCommand = new RelayCommand(() => _shell.OpenWindowsSoundSettings());
        BrowseEqualizerApoFolderCommand = new RelayCommand(BrowseEqualizerApoFolder);
        ClearEqualizerApoPathCommand = new RelayCommand(ClearEqualizerApoPath, () => !string.IsNullOrWhiteSpace(_settings.EqualizerApoPathOverride));
        DetectEngineCommand = new AsyncRelayCommand(DetectEngineAsync, () => !IsDetectingEngine);
        TestAudioEngineCommand = new AsyncRelayCommand(TestAudioEngineAsync, () => !IsDetectingEngine && !IsTestingAudioEngine);
        OpenEqualizerApoSiteCommand = new RelayCommand(() => _shell.OpenUrl(AppInfo.EqualizerApoProjectUrl));
        OpenEqualizerApoDownloadCommand = new RelayCommand(() => _shell.OpenUrl(AppInfo.EqualizerApoDownloadUrl));
        ApplyHotkeysCommand = new RelayCommand(() => _hotkeysChanged());
        ResetHotkeysCommand = new RelayCommand(ResetHotkeys);
        ExportBackupCommand = new RelayCommand(ExportBackup);
        ImportBackupCommand = new RelayCommand(ImportBackup);
        OpenDataFolderCommand = new RelayCommand(() => OpenFolderSafely(AppPaths.Root));
        OpenLogsFolderCommand = new RelayCommand(() => OpenFolderSafely(AppPaths.LogsDirectory));
        OpenLogFileCommand = new RelayCommand(() => OpenFileSafely(_log.LogPath));
        SaveDiagnosticsCommand = new RelayCommand(SaveDiagnostics);
        ResetDefaultsCommand = new RelayCommand(ResetDefaults);

        SyncStartupState();
        RefreshDevices(false);
        UpdateHotkeyValidation();
    }

    public RelayCommand RefreshDevicesCommand { get; }
    public RelayCommand SetWindowsDefaultCommand { get; }
    public RelayCommand OpenSoundSettingsCommand { get; }
    public RelayCommand BrowseEqualizerApoFolderCommand { get; }
    public RelayCommand ClearEqualizerApoPathCommand { get; }
    public AsyncRelayCommand DetectEngineCommand { get; }
    public AsyncRelayCommand TestAudioEngineCommand { get; }
    public RelayCommand OpenEqualizerApoSiteCommand { get; }
    public RelayCommand OpenEqualizerApoDownloadCommand { get; }
    public RelayCommand ApplyHotkeysCommand { get; }
    public RelayCommand ResetHotkeysCommand { get; }
    public RelayCommand ExportBackupCommand { get; }
    public RelayCommand ImportBackupCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }
    public RelayCommand OpenLogsFolderCommand { get; }
    public RelayCommand OpenLogFileCommand { get; }
    public RelayCommand SaveDiagnosticsCommand { get; }
    public RelayCommand ResetDefaultsCommand { get; }

    public ObservableCollection<AudioDeviceInfo> Devices
    {
        get => _devices;
        private set
        {
            if (!SetProperty(ref _devices, value)) return;
            OnPropertyChanged(nameof(DeviceCountText));
        }
    }

    public string DeviceCountText => Devices.Count == 1 ? "1 active output device" : $"{Devices.Count} active output devices";

    public AudioDeviceInfo? SelectedMonitoringDevice
    {
        get => _selectedMonitoringDevice;
        set
        {
            if (ReferenceEquals(_selectedMonitoringDevice, value)) return;
            _selectedMonitoringDevice = value;
            OnPropertyChanged(nameof(SelectedMonitoringDevice), nameof(SelectedDeviceSummary), nameof(DeviceAvailabilityText));
            if (_suppressMonitoringEvents || value is null) return;

            if (!MonitorDefaultDevice)
            {
                _settings.Audio.MonitoringDeviceId = value.Id;
                _audioRoutingChanged();
                _settingsChanged();
            }
        }
    }

    public bool MonitorDefaultDevice
    {
        get => _settings.Audio.MonitorDefaultDevice;
        set
        {
            if (_settings.Audio.MonitorDefaultDevice == value) return;
            _settings.Audio.MonitorDefaultDevice = value;
            if (value)
            {
                _settings.Audio.MonitoringDeviceId = null;
            }
            else
            {
                _settings.Audio.MonitoringDeviceId = _selectedMonitoringDevice?.Id;
            }

            OnPropertyChanged(nameof(MonitorDefaultDevice), nameof(DeviceComboEnabled), nameof(SelectedDeviceSummary),
                nameof(DeviceAvailabilityText));
            _audioRoutingChanged();
            _settingsChanged();
        }
    }

    public bool DeviceComboEnabled => !MonitorDefaultDevice;

    public string SelectedDeviceSummary => _selectedMonitoringDevice is null
        ? "No monitoring device selected"
        : $"{_selectedMonitoringDevice.Name} · {_selectedMonitoringDevice.FormatSummary}";

    public string DeviceAvailabilityText
    {
        get
        {
            if (DeviceComboEnabled && _selectedMonitoringDevice is null)
                return "The configured monitoring device is not currently connected.";
            if (_selectedMonitoringDevice?.IsDefault == true)
                return "This is the current Windows default output.";
            return "Measurement uses WASAPI loopback on the selected output.";
        }
    }

    /// <summary>
    /// Processing scope is independent from the monitoring device. "All devices" writes
    /// no Device line, so Equalizer APO applies the DSP to every APO-enabled output.
    /// </summary>
    public bool ApplyToAllDevices
    {
        get => _settings.Audio.ApplyToAllDevices;
        set
        {
            if (_settings.Audio.ApplyToAllDevices == value) return;

            if (value)
            {
                _settings.Audio.ApplyToAllDevices = true;
                _settings.Audio.ScopedDeviceId = null;
                _settings.Audio.ScopedDevicePattern = null;
            }
            else
            {
                _selectedProcessingDevice ??=
                    Devices.FirstOrDefault(d => d.ApoAssociated) ?? Devices.FirstOrDefault();
                if (_selectedProcessingDevice is null)
                {
                    _dialogs.ShowWarning("No audio outputs found",
                        "Windows did not report an active render device, so processing cannot be scoped.");
                    OnPropertyChanged(nameof(ApplyToAllDevices), nameof(ProcessingDeviceComboEnabled));
                    return;
                }

                _settings.Audio.ApplyToAllDevices = false;
                ApplyProcessingScope(_selectedProcessingDevice);
            }

            OnPropertyChanged(nameof(ApplyToAllDevices), nameof(ProcessingDeviceComboEnabled),
                nameof(SelectedProcessingDevice), nameof(ProcessingScopeSummary),
                nameof(HasProcessingApoWarning));
            _settingsChanged();
        }
    }

    public AudioDeviceInfo? SelectedProcessingDevice
    {
        get => _selectedProcessingDevice;
        set
        {
            if (ReferenceEquals(_selectedProcessingDevice, value)) return;
            _selectedProcessingDevice = value;
            if (!_settings.Audio.ApplyToAllDevices && value is not null)
            {
                ApplyProcessingScope(value);
                _settingsChanged();
            }

            OnPropertyChanged(nameof(SelectedProcessingDevice), nameof(ProcessingScopeSummary),
                nameof(HasProcessingApoWarning));
        }
    }

    public bool ProcessingDeviceComboEnabled => !ApplyToAllDevices;

    public string ProcessingScopeSummary
    {
        get
        {
            if (ApplyToAllDevices)
            {
                var enabled = Devices.Count(d => d.ApoAssociated);
                return $"All APO-enabled outputs · {enabled} of {Devices.Count} active output(s) have Equalizer APO enabled.";
            }

            return _selectedProcessingDevice is null
                ? "No processing output selected."
                : $"{_selectedProcessingDevice.Name} · {_selectedProcessingDevice.ApoStatusText}.";
        }
    }

    public bool HasProcessingApoWarning =>
        Devices.Count > 0 &&
        (ApplyToAllDevices
            ? Devices.All(d => !d.ApoAssociated)
            : _selectedProcessingDevice?.ApoAssociated == false);

    private void ApplyProcessingScope(AudioDeviceInfo device)
    {
        _settings.Audio.ScopedDeviceId = device.Id;
        _settings.Audio.ScopedDevicePattern = device.ScopePattern;
    }

    public bool StartWithWindows
    {
        get => _settings.Application.StartWithWindows;
        set
        {
            if (_settings.Application.StartWithWindows == value) return;
            try
            {
                _startup.Apply(value, StartMinimized);
                _settings.Application.StartWithWindows = value;
                OnPropertyChanged(nameof(StartWithWindows));
                _settingsChanged();
            }
            catch (Exception ex)
            {
                _log.Error("Could not update the Windows startup entry.", ex);
                _dialogs.ShowError("Could not change startup",
                    "Boost Studio could not update the Windows startup entry.", ex.Message);
                OnPropertyChanged(nameof(StartWithWindows));
            }
        }
    }

    public bool StartMinimized
    {
        get => _settings.Application.StartMinimized;
        set
        {
            if (_settings.Application.StartMinimized == value) return;
            _settings.Application.StartMinimized = value;
            OnPropertyChanged(nameof(StartMinimized));
            if (StartWithWindows)
            {
                try
                {
                    _startup.Apply(true, value);
                }
                catch (Exception ex)
                {
                    _log.Error("Could not update the startup command.", ex);
                }
            }

            _settingsChanged();
        }
    }

    public bool MinimizeToTray
    {
        get => _settings.Application.MinimizeToTray;
        set
        {
            if (_settings.Application.MinimizeToTray == value) return;
            _settings.Application.MinimizeToTray = value;
            OnPropertyChanged(nameof(MinimizeToTray));
            _settingsChanged();
        }
    }

    public bool CloseToTray
    {
        get => _settings.Application.CloseToTray;
        set
        {
            if (_settings.Application.CloseToTray == value) return;
            _settings.Application.CloseToTray = value;
            OnPropertyChanged(nameof(CloseToTray));
            _settingsChanged();
        }
    }

    public bool ShowTrayNotifications
    {
        get => _settings.Application.ShowTrayNotifications;
        set
        {
            if (_settings.Application.ShowTrayNotifications == value) return;
            _settings.Application.ShowTrayNotifications = value;
            OnPropertyChanged(nameof(ShowTrayNotifications));
            _settingsChanged();
        }
    }

    public bool ShowClipWarnings
    {
        get => _settings.Application.ShowClipWarnings;
        set
        {
            if (_settings.Application.ShowClipWarnings == value) return;
            _settings.Application.ShowClipWarnings = value;
            OnPropertyChanged(nameof(ShowClipWarnings));
            _settingsChanged();
        }
    }

    public bool HotkeysEnabled
    {
        get => _settings.Hotkeys.Enabled;
        set
        {
            if (_settings.Hotkeys.Enabled == value) return;
            _settings.Hotkeys.Enabled = value;
            OnPropertyChanged(nameof(HotkeysEnabled), nameof(HotkeyFieldsEnabled));
            MarkHotkeysDirty();
            _settingsChanged();
        }
    }

    public bool HotkeyFieldsEnabled => HotkeysEnabled;

    public string ToggleBassHotkey
    {
        get => _settings.Hotkeys.ToggleBass;
        set
        {
            if (_settings.Hotkeys.ToggleBass == value) return;
            _settings.Hotkeys.ToggleBass = value;
            OnPropertyChanged(nameof(ToggleBassHotkey));
            MarkHotkeysDirty();
            _settingsChanged();
        }
    }

    public string ToggleVolumeHotkey
    {
        get => _settings.Hotkeys.ToggleVolume;
        set
        {
            if (_settings.Hotkeys.ToggleVolume == value) return;
            _settings.Hotkeys.ToggleVolume = value;
            OnPropertyChanged(nameof(ToggleVolumeHotkey));
            MarkHotkeysDirty();
            _settingsChanged();
        }
    }

    public string ToggleProcessingHotkey
    {
        get => _settings.Hotkeys.ToggleProcessing;
        set
        {
            if (_settings.Hotkeys.ToggleProcessing == value) return;
            _settings.Hotkeys.ToggleProcessing = value;
            OnPropertyChanged(nameof(ToggleProcessingHotkey));
            MarkHotkeysDirty();
            _settingsChanged();
        }
    }

    public string HotkeyValidationText { get; private set; } = string.Empty;
    public bool HasHotkeyValidationError { get; private set; }

    public string HotkeyStatus
    {
        get => _hotkeyStatus;
        private set => SetProperty(ref _hotkeyStatus, value);
    }

    public bool HotkeyStatusIsError
    {
        get => _hotkeyStatusIsError;
        private set => SetProperty(ref _hotkeyStatusIsError, value);
    }

    public string EqualizerApoPathOverride
    {
        get => _settings.EqualizerApoPathOverride ?? string.Empty;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(_settings.EqualizerApoPathOverride, normalized, StringComparison.OrdinalIgnoreCase))
                return;

            _settings.EqualizerApoPathOverride = normalized;
            OnPropertyChanged(nameof(EqualizerApoPathOverride), nameof(HasEqualizerApoPathOverride),
                nameof(EnginePathDescription));
            ClearEqualizerApoPathCommand.RaiseCanExecuteChanged();
            _settingsChanged();
        }
    }

    public bool HasEqualizerApoPathOverride => !string.IsNullOrWhiteSpace(_settings.EqualizerApoPathOverride);

    public string EnginePathDescription => HasEqualizerApoPathOverride
        ? "Using a custom Equalizer APO location."
        : "Auto-detected from the Equalizer APO registry or standard install folders.";

    private bool _isDetectingEngine;
    public bool IsDetectingEngine
    {
        get => _isDetectingEngine;
        private set
        {
            if (!SetProperty(ref _isDetectingEngine, value)) return;
            DetectEngineCommand.RaiseCanExecuteChanged();
            TestAudioEngineCommand.RaiseCanExecuteChanged();
        }
    }

    private bool _isTestingAudioEngine;
    public bool IsTestingAudioEngine
    {
        get => _isTestingAudioEngine;
        private set
        {
            if (!SetProperty(ref _isTestingAudioEngine, value)) return;
            TestAudioEngineCommand.RaiseCanExecuteChanged();
        }
    }

    public string LastBackupText
    {
        get => _lastBackupText;
        private set => SetProperty(ref _lastBackupText, value);
    }

    public string DataFolderPath => AppPaths.Root;
    public string LogFilePath => _log.LogPath;
    public string VersionText => $"Version {AppInfo.Version}";
    public string ProductText => AppInfo.ProductName;
    public string Tagline => AppInfo.Tagline;
    public string OperatingSystemText => Environment.OSVersion.VersionString;
    public string ArchitectureText => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
    public string RuntimeText => System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;

    public void RefreshDevices(bool interactive)
    {
        var preserveId = _settings.Audio.MonitorDefaultDevice
            ? _audioDevices.GetDefaultDeviceId()
            : _settings.Audio.MonitoringDeviceId;

        var list = _audioDevices.GetRenderDevices();
        _suppressMonitoringEvents = true;
        try
        {
            Devices = new ObservableCollection<AudioDeviceInfo>(list);
            var selected = list.FirstOrDefault(d => string.Equals(d.Id, preserveId, StringComparison.OrdinalIgnoreCase))
                           ?? list.FirstOrDefault(d => d.IsDefault)
                           ?? list.FirstOrDefault();

            _selectedMonitoringDevice = selected;
            SyncProcessingSelection(list);
            OnPropertyChanged(nameof(SelectedMonitoringDevice), nameof(SelectedDeviceSummary),
                nameof(DeviceAvailabilityText), nameof(DeviceCountText), nameof(SelectedProcessingDevice),
                nameof(ProcessingScopeSummary), nameof(HasProcessingApoWarning));
        }
        finally
        {
            _suppressMonitoringEvents = false;
        }

        if (interactive && Devices.Count == 0)
            _dialogs.ShowWarning("No audio outputs found",
                "Windows did not report an active render device. Check that an output device is connected and enabled.");

        UpdateMonitoringState();
    }

    public void RefreshDevicesSilently()
    {
        var selectedId = _selectedMonitoringDevice?.Id;
        var list = _audioDevices.GetRenderDevices();
        _suppressMonitoringEvents = true;
        try
        {
            Devices = new ObservableCollection<AudioDeviceInfo>(list);
            _selectedMonitoringDevice = list.FirstOrDefault(d =>
                                            string.Equals(d.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                                        ?? list.FirstOrDefault(d => d.IsDefault)
                                        ?? list.FirstOrDefault();
            SyncProcessingSelection(list);
            OnPropertyChanged(nameof(SelectedMonitoringDevice), nameof(SelectedDeviceSummary),
                nameof(DeviceAvailabilityText), nameof(DeviceCountText), nameof(SelectedProcessingDevice),
                nameof(ProcessingScopeSummary), nameof(HasProcessingApoWarning));
        }
        finally
        {
            _suppressMonitoringEvents = false;
        }
    }

    private void UpdateMonitoringState()
    {
        OnPropertyChanged(nameof(MonitorDefaultDevice), nameof(DeviceComboEnabled), nameof(SelectedDeviceSummary),
            nameof(DeviceAvailabilityText), nameof(DeviceCountText));
    }

    private void SyncProcessingSelection(IReadOnlyList<AudioDeviceInfo> devices)
    {
        var id = _settings.Audio.ScopedDeviceId;
        var pattern = _settings.Audio.ScopedDevicePattern;

        _selectedProcessingDevice =
            devices.FirstOrDefault(d => !string.IsNullOrWhiteSpace(id) &&
                                       string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? devices.FirstOrDefault(d => !string.IsNullOrWhiteSpace(pattern) && DeviceMatchesScope(d, pattern))
            ?? devices.FirstOrDefault(d => d.ApoAssociated)
            ?? devices.FirstOrDefault();
    }

    private static bool DeviceMatchesScope(AudioDeviceInfo device, string pattern)
    {
        var haystack = $"{device.DeviceName} {device.Name} {device.Id}";
        var tokens = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length > 0 &&
               tokens.All(token => haystack.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private void SetWindowsDefault()
    {
        var device = _selectedMonitoringDevice;
        if (device is null)
        {
            _dialogs.ShowWarning("No device selected", "Choose an output device first.");
            return;
        }

        if (!_dialogs.Confirm("Change the Windows default output?",
                $"Make “{device.Name}” the Windows default output for all applications? Audio will be redirected immediately.",
                "Change default", "Cancel"))
            return;

        var result = _audioDevices.TrySetDefaultDevice(device.Id);
        if (result.Success)
        {
            RefreshDevices(false);
            _dialogs.ShowSuccess("Default output changed", result.Message);
            if (MonitorDefaultDevice)
                _audioRoutingChanged();
        }
        else
        {
            _dialogs.ShowWarning("Could not change the default output", result.Message,
                "Windows Settings → System → Sound → Output lets you choose the default device.");
        }
    }

    private void BrowseEqualizerApoFolder()
    {
        var initial = Directory.Exists(_settings.EqualizerApoPathOverride)
            ? _settings.EqualizerApoPathOverride
            : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select the Equalizer APO installation or config folder",
            InitialDirectory = initial,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true) return;
        EqualizerApoPathOverride = dialog.FolderName;
    }

    private void ClearEqualizerApoPath()
    {
        EqualizerApoPathOverride = string.Empty;
    }

    private async Task DetectEngineAsync()
    {
        IsDetectingEngine = true;
        try
        {
            var status = await _engine.RefreshAsync(_settings);
            _dialogs.ShowInfo("Engine detection finished", status.Summary, status.Detail);
        }
        catch (Exception ex)
        {
            _log.Error("Engine detection failed.", ex);
            _dialogs.ShowError("Could not detect Equalizer APO", ex.Message);
        }
        finally
        {
            IsDetectingEngine = false;
        }
    }

    private async Task TestAudioEngineAsync()
    {
        IsTestingAudioEngine = true;
        try
        {
            // Keep the UI responsive while tones play and configs are swapped.
            var result = await Task.Run(() => _engine.TestAudioProcessingAsync(_settings));
            var message = new StringBuilder();
            message.AppendLine($"Equalizer APO detected: {(result.ApoInstalled ? "✓" : "✗")}");
            message.AppendLine($"APO attached to target output: {(result.CorrectEndpoint ? "✓" : "✗")}");
            message.AppendLine($"DSP configuration written: {(result.ConfigWritten ? "✓" : "✗")}");
            message.AppendLine($"Loopback tone captured: {(result.GainChangeMeasured || result.Steps?.Count > 0 ? "✓" : "✗")}");
            message.AppendLine(result.GainChangeMeasured
                ? $"-18 dB test measured: {result.MeasuredDeltaDb:+0.0;-0.0;0.0} dB (expected -18.0 dB) ✓"
                : "-18 dB test measured: no reliable change ✗");
            message.AppendLine(result.BassResponseMeasured
                ? "Bass response measured: ✓"
                : "Bass response measured: ✗");
            message.AppendLine(result.VolumeGainMeasured
                ? "Volume gain measured: ✓"
                : "Volume gain measured: ✗");
            message.AppendLine($"Bass and volume tested independently: {(result.BoostersIndependent ? "✓" : "✗")}");
            if (!string.IsNullOrWhiteSpace(result.DeviceName))
                message.AppendLine($"Loopback device: {result.DeviceName}");
            if (!string.IsNullOrWhiteSpace(result.ReportPath))
                message.AppendLine($"Report: {result.ReportPath}");
            message.AppendLine();
            if (!string.IsNullOrWhiteSpace(result.Detail))
                message.AppendLine(result.Detail);
            message.AppendLine();
            message.AppendLine("Manual listening check: play music, then toggle Bass Booster and Volume Booster separately. The loopback test proves the engine path; your ears confirm the result on your speakers/headphones.");

            if (result.ProcessingVerified && result.GainChangeMeasured &&
                result.BassResponseMeasured && result.VolumeGainMeasured)
                _dialogs.ShowSuccess("Audio Engine Test Passed", message.ToString());
            else if (result.ProcessingVerified)
                _dialogs.ShowWarning("Audio Engine Partially Verified", message.ToString());
            else
                _dialogs.ShowError("Audio Engine Test Failed", message.ToString());
        }
        catch (Exception ex)
        {
            _log.Error("Audio engine test failed.", ex);
            _dialogs.ShowError("Audio engine test failed", ex.Message);
        }
        finally
        {
            IsTestingAudioEngine = false;
        }
    }

    private void MarkHotkeysDirty()
    {
        UpdateHotkeyValidation();
        HotkeyStatus = HotkeysEnabled
            ? "Shortcut changes are applied automatically."
            : "Global shortcuts are turned off.";
        HotkeyStatusIsError = HasHotkeyValidationError;
        _hotkeysChanged();
    }

    private void UpdateHotkeyValidation()
    {
        if (!HotkeysEnabled)
        {
            HotkeyValidationText = "Global shortcuts are disabled.";
            HasHotkeyValidationError = false;
            OnPropertyChanged(nameof(HotkeyValidationText), nameof(HasHotkeyValidationError));
            return;
        }

        var definitions = new List<(string Label, string Gesture, HotkeyDefinition Definition)>();
        var errors = new List<string>();
        foreach (var (label, gesture) in new[]
                 {
                     ("Bass", ToggleBassHotkey),
                     ("Volume", ToggleVolumeHotkey),
                     ("Master", ToggleProcessingHotkey)
                 })
        {
            if (!HotkeyService.TryParse(gesture, out var definition, out var error))
            {
                errors.Add($"{label}: {error}");
                continue;
            }

            if (definitions.Any(d => d.Definition.Modifiers == definition.Modifiers &&
                                     d.Definition.Key == definition.Key))
            {
                errors.Add($"{definition.DisplayName} is assigned more than once.");
                continue;
            }

            definitions.Add((label, gesture, definition));
        }

        HasHotkeyValidationError = errors.Count > 0;
        HotkeyValidationText = errors.Count > 0
            ? string.Join(" ", errors)
            : "Press Apply shortcuts after editing. Combinations require a modifier.";
        OnPropertyChanged(nameof(HotkeyValidationText), nameof(HasHotkeyValidationError));
    }

    private void ResetHotkeys()
    {
        _settings.Hotkeys.ToggleBass = "Ctrl+Alt+B";
        _settings.Hotkeys.ToggleVolume = "Ctrl+Alt+V";
        _settings.Hotkeys.ToggleProcessing = "Ctrl+Alt+M";
        OnPropertyChanged(nameof(ToggleBassHotkey), nameof(ToggleVolumeHotkey), nameof(ToggleProcessingHotkey));
        MarkHotkeysDirty();
        _settingsChanged();
    }

    public void SetHotkeyStatus(string message, bool isError)
    {
        HotkeyStatus = message;
        HotkeyStatusIsError = isError;
    }

    private void ExportBackup()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export Boost Studio backup",
            FileName = $"booststudio-backup-{DateTime.Now:yyyyMMdd}.boostbackup",
            DefaultExt = ".boostbackup",
            Filter = "Boost Studio backup (*.boostbackup)|*.boostbackup|Zip archive (*.zip)|*.zip|All files (*.*)|*.*",
            AddExtension = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (dialog.ShowDialog() != true) return;
        try
        {
            var diagnostics = BuildDiagnostics();
            _backup.Export(dialog.FileName, _settings, GetLibrary(), diagnostics, GetProfiles());
            LastBackupText = $"Exported {DateTime.Now:HH:mm} · {dialog.FileName}";
            _dialogs.ShowSuccess("Backup exported",
                "Your settings, sound profiles and user equalizer presets were saved.",
                dialog.FileName);
        }
        catch (Exception ex)
        {
            _log.Error("Backup export failed.", ex);
            _dialogs.ShowError("Backup failed", "The backup could not be written.", ex.Message);
        }
    }

    private void ImportBackup()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import a Boost Studio backup",
            Filter = "Boost Studio backup (*.boostbackup)|*.boostbackup|Zip archive (*.zip)|*.zip|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true) return;
        var result = _backup.Import(dialog.FileName);
        if (!result.Success || result.Settings is null)
        {
            _dialogs.ShowError("Backup could not be read", result.Message);
            return;
        }

        if (!_dialogs.Confirm("Replace current settings?",
                "Importing this backup replaces the current settings, sound profiles and user equalizer presets. Export a backup first if you want to keep the current state.",
                "Import", "Cancel", danger: true))
            return;

        try
        {
            _settings.CopyFrom(result.Settings);
            var presets = result.Presets ?? new UserPresetLibrary();
            _backup.ApplyImported(_settings, presets, result.Profiles);
            try
            {
                _startup.Apply(_settings.Application.StartWithWindows, _settings.Application.StartMinimized);
            }
            catch (Exception ex)
            {
                _log.Warn($"Could not sync the startup entry after import: {ex.Message}");
            }

            _libraryReplaced(presets);
            // Old backups do not contain profiles. Preserve the current library in
            // that case instead of silently deleting the user's saved setups.
            if (result.Profiles is not null)
                _profileLibraryReplaced(result.Profiles);
            _reloadAll();
            _dialogs.ShowSuccess("Backup restored", "Settings, profiles and presets were imported successfully.");
        }
        catch (Exception ex)
        {
            _log.Error("Backup import failed.", ex);
            _dialogs.ShowError("Import failed", "The backup could not be applied.", ex.Message);
        }
    }

    private void SaveDiagnostics()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DiagnosticsDirectory);
            var path = Path.Combine(AppPaths.DiagnosticsDirectory,
                $"booststudio-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            var content = BuildDiagnostics();
            File.WriteAllText(path, content, new UTF8Encoding(false));
            _dialogs.ShowSuccess("Diagnostics saved",
                "A local diagnostics report was written. Review it before sharing; it contains device names and settings.",
                path);
            _shell.OpenFolder(AppPaths.DiagnosticsDirectory);
        }
        catch (Exception ex)
        {
            _log.Error("Could not save diagnostics.", ex);
            _dialogs.ShowError("Diagnostics failed", ex.Message);
        }
    }

    public string BuildDiagnostics()
    {
        var status = _engine.Status;
        var builder = new StringBuilder();
        builder.AppendLine($"{AppInfo.ProductName} {AppInfo.Version} diagnostics");
        builder.AppendLine($"Generated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"OS: {Environment.OSVersion.VersionString} ({ArchitectureText})");
        builder.AppendLine($"Runtime: {RuntimeText}");
        builder.AppendLine($"Elevated: {_shell.IsElevated}");
        builder.AppendLine($"Process: {_shell.ExecutablePath}");
        builder.AppendLine();
        builder.AppendLine("Engine");
        builder.AppendLine($"  State: {status.State}");
        builder.AppendLine($"  Installed: {status.Installed}");
        builder.AppendLine($"  Version: {status.Version ?? "unknown"}");
        builder.AppendLine($"  Install: {status.InstallDirectory ?? "not detected"}");
        builder.AppendLine($"  Config: {status.ConfigDirectory ?? "not detected"}");
        builder.AppendLine($"  Config writable: {status.ConfigWritable}");
        builder.AppendLine($"  Integration active: {status.IntegrationActive}");
        builder.AppendLine($"  Managed file present: {status.ManagedFilePresent}");
        builder.AppendLine($"  Active filters: {status.ActiveFilterCount}");
        builder.AppendLine($"  Preamp: {status.PreampDb:0.0} dB");
        builder.AppendLine($"  Headroom reserved: {status.HeadroomReservedDb:0.0} dB");
        builder.AppendLine($"  Summary: {status.Summary}");
        if (!string.IsNullOrWhiteSpace(status.Detail))
            builder.AppendLine($"  Detail: {status.Detail}");
        builder.AppendLine();
        builder.AppendLine("Monitoring");
        builder.AppendLine($"  Running: {_monitor.IsActive}");
        builder.AppendLine($"  Device: {_monitor.DeviceName}");
        builder.AppendLine($"  Format: {_monitor.FormatSummary}");
        builder.AppendLine($"  Status: {_monitor.StatusText}");
        builder.AppendLine($"  Peak: {_monitor.PeakText} · RMS: {_monitor.RmsText}");
        builder.AppendLine();
        builder.AppendLine("Devices");
        if (Devices.Count == 0)
        {
            builder.AppendLine("  (none reported)");
        }
        else
        {
            foreach (var device in Devices)
                builder.AppendLine($"  {(device.IsDefault ? "*" : " ")} {device.Name} | {device.FormatSummary} | {device.State} | {device.Id}");
        }

        builder.AppendLine();
        builder.AppendLine("Settings summary");
        builder.AppendLine($"  Processing enabled: {_settings.ProcessingEnabled}");
        builder.AppendLine($"  Auto headroom: {_settings.AutoHeadroom}");
        builder.AppendLine($"  Bass: enabled={_settings.Bass.Enabled}, intensity={_settings.Bass.Intensity}%, frequency={_settings.Bass.FrequencyHz} Hz, maxGain={_settings.Bass.MaxGainDb} dB, Q={_settings.Bass.ShelfQ}, protectHeadroom={_settings.Bass.ProtectHeadroom}, preset={_settings.Bass.PresetName}");
        builder.AppendLine($"  Volume: enabled={_settings.Volume.Enabled}, boost={_settings.Volume.BoostPercent}% of {_settings.Volume.CeilingDb} dB, trim={_settings.Volume.PreampDb} dB, loudness={_settings.Volume.Loudness}%, AGC={_settings.Volume.AutoGainControl}");
        builder.AppendLine($"  Equalizer: enabled={_settings.Equalizer.Enabled}, preset={_settings.Equalizer.SelectedPresetName}, gains=[{string.Join(", ", _settings.Equalizer.GainsDb.Select(g => g.ToString("0.0", CultureInfo.InvariantCulture)))}]");
        builder.AppendLine($"  Monitoring default device: {_settings.Audio.MonitorDefaultDevice}");
        builder.AppendLine($"  Monitoring device id: {_settings.Audio.MonitoringDeviceId ?? "(follow default)"}");
        builder.AppendLine($"  Apply to all devices: {_settings.Audio.ApplyToAllDevices}");
        builder.AppendLine($"  Start with Windows: {_settings.Application.StartWithWindows}");
        builder.AppendLine($"  Hotkeys enabled: {_settings.Hotkeys.Enabled}");
        builder.AppendLine();
        builder.AppendLine($"Log: {_log.LogPath}");
        builder.AppendLine($"Data folder: {AppPaths.Root}");
        return builder.ToString();
    }

    private void ResetDefaults()
    {
        if (!_dialogs.Confirm("Reset Boost Studio settings?",
                "All module settings, audio routing, shortcuts and application preferences will return to their defaults. User equalizer presets and backup files are kept. Boost Studio will reconnect the audio engine with default settings.",
                "Reset everything", "Cancel", danger: true))
            return;

        try
        {
            var defaults = AppSettings.CreateDefault();
            defaults.Application.HasSeenWelcome = true;
            _settings.CopyFrom(defaults);
            try
            {
                _startup.Apply(false, false);
            }
            catch (Exception ex)
            {
                _log.Warn($"Could not remove the startup entry during reset: {ex.Message}");
            }

            _backup.ApplyImported(_settings, GetLibrary());
            _reloadAll();
            _settingsChanged();
            _dialogs.ShowSuccess("Settings reset", "Boost Studio is back to its default configuration.");
        }
        catch (Exception ex)
        {
            _log.Error("Could not reset settings.", ex);
            _dialogs.ShowError("Reset failed", ex.Message);
        }
    }

    private UserPresetLibrary GetLibrary() => _libraryProvider();

    private ProfileLibrary GetProfiles() => _profileLibraryProvider();

    private void SyncStartupState()
    {
        var actual = _startup.IsEnabled();
        if (_settings.Application.StartWithWindows && !actual)
        {
            try
            {
                _startup.Apply(true, _settings.Application.StartMinimized);
                actual = true;
            }
            catch (Exception ex)
            {
                _log.Warn($"Could not restore the startup entry: {ex.Message}");
                actual = false;
            }
        }
        else if (!_settings.Application.StartWithWindows && actual)
        {
            try
            {
                _startup.Apply(false, false);
                actual = false;
            }
            catch (Exception ex)
            {
                _log.Warn($"Could not remove a stale startup entry: {ex.Message}");
            }
        }

        _settings.Application.StartWithWindows = actual;
    }

    public void ReloadFromSettings()
    {
        RefreshDevices(false);
        UpdateHotkeyValidation();
        OnPropertyChanged(nameof(StartWithWindows), nameof(StartMinimized), nameof(MinimizeToTray),
            nameof(CloseToTray), nameof(ShowTrayNotifications), nameof(ShowClipWarnings), nameof(HotkeysEnabled),
            nameof(HotkeyFieldsEnabled), nameof(ToggleBassHotkey), nameof(ToggleVolumeHotkey),
            nameof(ToggleProcessingHotkey), nameof(EqualizerApoPathOverride), nameof(HasEqualizerApoPathOverride),
            nameof(EnginePathDescription), nameof(SelectedMonitoringDevice), nameof(MonitorDefaultDevice),
            nameof(DeviceComboEnabled), nameof(SelectedDeviceSummary), nameof(DeviceAvailabilityText),
            nameof(ApplyToAllDevices), nameof(SelectedProcessingDevice), nameof(ProcessingDeviceComboEnabled),
            nameof(ProcessingScopeSummary), nameof(HasProcessingApoWarning));
        ClearEqualizerApoPathCommand.RaiseCanExecuteChanged();
    }

    private void OpenFolderSafely(string path)
    {
        if (!_shell.OpenFolder(path))
            _dialogs.ShowWarning("Could not open folder", path);
    }

    private void OpenFileSafely(string path)
    {
        if (!_shell.OpenFile(path))
            _dialogs.ShowInfo("File not found", path);
    }
}
