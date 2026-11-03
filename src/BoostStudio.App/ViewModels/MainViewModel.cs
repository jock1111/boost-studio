using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using BoostStudio.App.Infrastructure;
using BoostStudio.App.Services;
using BoostStudio.Core.Audio;
using BoostStudio.Core.Dsp;
using BoostStudio.Core.Engine;
using BoostStudio.Core.Models;
using BoostStudio.Core.Storage;

namespace BoostStudio.App.ViewModels;

public sealed record NavigationItem(string Key, string Label, string Subtitle, string Glyph);

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan ClipNotificationInterval = TimeSpan.FromSeconds(30);

    private readonly ILogService _log;
    private readonly bool _startMinimizedByArgument;
    private readonly SettingsStore _settingsStore;
    private readonly PresetStore _presetStore;
    private readonly BackupService _backup;
    private readonly AudioEngineService _engine;
    private readonly AudioMonitorService _monitorService;
    private readonly TestToneService _testToneService;
    private readonly AudioDeviceService _audioDevices;
    private readonly StartupService _startup;
    private readonly ShellService _shell;
    private readonly IDialogService _dialogs;
    private readonly HotkeyService _hotkeys;
    private readonly TrayIconService _tray;
    private readonly AutoGainController _autoGain;
    private readonly AutoSaver _autoSaver;
    private readonly DispatcherTimer _devicePollTimer;
    private readonly DispatcherTimer _monitorRestartTimer;
    private readonly SemaphoreSlim _monitorGate = new(1, 1);

    private AppSettings _settings;
    private UserPresetLibrary _presets;
    private NavigationItem _selectedNavigation;
    private object _currentPage;
    private bool _initialized;
    private bool _welcomeShown;
    private bool _disposed;
    private bool _exitRequested;
    private IntPtr _windowHandle;
    private int _monitorGeneration;
    private int _frameDispatchPending;
    private SpectrumFrame? _latestFrame;
    private DateTime _lastClipNotificationUtc = DateTime.MinValue;
    private string? _lastDefaultDeviceId;
    private int _devicePollTicks;

    public MainViewModel(ILogService log, string[]? args = null)
    {
        _log = log;
        var arguments = args ?? Array.Empty<string>();
        _startMinimizedByArgument = arguments.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));

        AppPaths.EnsureCreated();
        _settingsStore = new SettingsStore(log: log);
        _presetStore = new PresetStore(log: log);
        _settings = _settingsStore.Load();
        _presets = _presetStore.Load();
        _backup = new BackupService(_settingsStore, _presetStore, log);

        _dialogs = new DialogService();
        _shell = new ShellService();
        _startup = new StartupService(_shell);
        _audioDevices = new AudioDeviceService(log);
        _engine = new AudioEngineService(log);
        _monitorService = new AudioMonitorService(log);
        _testToneService = new TestToneService(log);
        _hotkeys = new HotkeyService();
        _tray = new TrayIconService();
        _autoGain = new AutoGainController();
        _autoSaver = new AutoSaver(SaveSettings, TimeSpan.FromMilliseconds(650), log);

        Monitor = new MonitorViewModel();
        TestTone = new TestToneViewModel(_testToneService);
        Bass = new BassBoosterViewModel(_settings, Monitor, TestTone, OnSettingsMutated);
        Volume = new VolumeBoosterViewModel(_settings, Monitor, TestTone, _autoGain, OnSettingsMutated);
        Equalizer = new EqualizerViewModel(_settings, _presets, _presetStore, _dialogs, OnSettingsMutated);
        Engine = new EngineStatusViewModel(_engine, _settings, _dialogs, _shell, log,
            () => _autoGain.TrimDb, RestartElevatedAndExit);
        Settings = new SettingsViewModel(
            _settings, _dialogs, log, _shell, _startup, _audioDevices, _backup, _engine, Monitor,
            OnSettingsMutated, ScheduleMonitorRestart, ApplyHotkeysNow, ReloadAllFromSettings,
            library =>
            {
                _presets = library;
                Equalizer.ReplaceLibrary(library);
            },
            () => _presets);

        Dashboard = new DashboardViewModel(Bass, Volume, Equalizer, Monitor, TestTone, Engine,
            () => _settings.ProcessingEnabled, v => SetProcessingEnabled(v), Navigate);

        NavigateCommand = new RelayCommand<string>(key =>
        {
            if (!string.IsNullOrWhiteSpace(key)) Navigate(key);
        });

        NavigationItems = new ObservableCollection<NavigationItem>
        {
            new("dashboard", "Dashboard", "Overview", "\uE80F"),
            new("bass", "Bass Booster", "Low-shelf weight and impact", "\uE8D6"),
            new("volume", "Volume Booster", "Gain, loudness and metering", "\uE767"),
            new("equalizer", "Equalizer", "Ten ISO bands", "\uE9E9"),
            new("settings", "Settings", "Engine, devices and app", "\uE713")
        };

        _selectedNavigation = NavigationItems.FirstOrDefault(i => i.Key == _settings.LastPage) ?? NavigationItems[0];
        _currentPage = PageForKey(_selectedNavigation.Key);

        _engine.StatusChanged += OnEngineStatusChanged;
        _monitorService.FrameReady += OnFrameReady;
        _monitorService.MonitorStopped += OnMonitorStopped;
        _autoGain.TrimChanged += OnAutoGainTrimChanged;
        _hotkeys.Pressed += OnHotkeyPressed;

        _tray.ShowRequested += (_, _) => ShowWindowRequested?.Invoke(this, EventArgs.Empty);
        _tray.SettingsRequested += (_, _) =>
        {
            Navigate("settings");
            ShowWindowRequested?.Invoke(this, EventArgs.Empty);
        };
        _tray.ToggleProcessingRequested += (_, _) => ToggleProcessingFromShell();
        _tray.ToggleBassRequested += (_, _) => ToggleBassFromShell();
        _tray.ToggleVolumeRequested += (_, _) => ToggleVolumeFromShell();
        _tray.ToggleEqualizerRequested += (_, _) => ToggleEqualizerFromShell();
        _tray.ExitRequested += (_, _) => RequestExit();

        _devicePollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _devicePollTimer.Tick += OnDevicePollTick;
        _monitorRestartTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _monitorRestartTimer.Tick += OnMonitorRestartTimerTick;

        Monitor.WarningsEnabled = _settings.Application.ShowClipWarnings;
        UpdateTrayState();
        var elevated = _shell.IsElevated ? "elevated" : "standard";
        _log.Info($"Boost Studio {AppInfo.Version} started ({elevated}); settings loaded from {_settingsStore.FilePath}.");
    }

    public BassBoosterViewModel Bass { get; }
    public VolumeBoosterViewModel Volume { get; }
    public EqualizerViewModel Equalizer { get; }
    public MonitorViewModel Monitor { get; }
    public TestToneViewModel TestTone { get; }
    public EngineStatusViewModel Engine { get; }
    public SettingsViewModel Settings { get; }
    public DashboardViewModel Dashboard { get; }
    public ObservableCollection<NavigationItem> NavigationItems { get; }

    public bool StartMinimizedRequested => _startMinimizedByArgument || _settings.Application.StartMinimized;

    public NavigationItem SelectedNavigation
    {
        get => _selectedNavigation;
        set
        {
            if (value is null || ReferenceEquals(_selectedNavigation, value)) return;
            _selectedNavigation = value;
            _settings.LastPage = value.Key;
            _currentPage = PageForKey(value.Key);
            OnPropertyChanged(nameof(SelectedNavigation), nameof(CurrentPage), nameof(CurrentPageTitle),
                nameof(CurrentPageSubtitle));
            _autoSaver.Schedule();
        }
    }

    public object CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public string CurrentPageTitle => _selectedNavigation.Label;
    public string CurrentPageSubtitle => _selectedNavigation.Subtitle;

    public bool ProcessingEnabled
    {
        get => _settings.ProcessingEnabled;
        set => SetProcessingEnabled(value, notify: true);
    }

    public string MasterStatusText => _settings.ProcessingEnabled ? "MASTER ON" : "MASTER BYPASSED";
    public string MasterDescription => _settings.ProcessingEnabled
        ? "Boost Studio processing is applied to the selected output."
        : "All Boost Studio processing is bypassed. Module settings remain saved.";
    public string EngineSummary => _engine.Status.Summary;
    public string VersionText => $"v{AppInfo.Version}";

    public RelayCommand<string> NavigateCommand { get; }

    public event EventHandler? ShowWindowRequested;
    public event EventHandler? ExitRequested;

    public void AttachWindow(IntPtr handle)
    {
        _windowHandle = handle;
        _hotkeys.Attach(handle);
        if (_initialized)
            ApplyHotkeysNow();
    }

    public async Task InitializeAsync()
    {
        if (_initialized || _disposed) return;
        _initialized = true;

        try
        {
            SyncRuntimeState();
            await _engine.ApplyAsync(_settings, _autoGain.TrimDb);
            ApplyHotkeysNow();
            _devicePollTimer.Start();
            await RestartMonitorAsync();
            _lastDefaultDeviceId = _audioDevices.GetDefaultDeviceId();
            Dashboard.Refresh();
            UpdateTrayState();
            _autoSaver.Schedule();
            await MaybeShowWelcomeAsync();
        }
        catch (Exception ex)
        {
            _log.Error("Boost Studio initialization failed.", ex);
            _dialogs.ShowError("Boost Studio could not finish starting",
                "Some features may be unavailable until you restart the app.", ex.Message);
        }
    }

    public async Task MaybeShowWelcomeAsync()
    {
        if (_disposed || _welcomeShown || _settings.Application.HasSeenWelcome) return;
        if (Application.Current?.MainWindow is not { IsVisible: true }) return;

        _welcomeShown = true;
        if (Engine.Status.Installed) return;

        var choice = _dialogs.ShowWelcome(Engine.Status);
        switch (choice)
        {
            case WelcomeChoice.Download:
                if (!_shell.OpenUrl(AppInfo.EqualizerApoDownloadUrl))
                    _dialogs.ShowError("Could not open the download page",
                        "Open this address in your browser:", AppInfo.EqualizerApoDownloadUrl);
                break;
            case WelcomeChoice.Recheck:
                var refreshed = await _engine.RefreshAsync(_settings);
                if (refreshed.Installed)
                {
                    if (_settings.AnyProcessingEnabled)
                        await _engine.ReconnectAsync(_settings, _autoGain.TrimDb);
                    _dialogs.ShowSuccess("Equalizer APO found",
                        "The engine is ready. Enable a module and connect to begin processing.");
                }
                else
                {
                    _dialogs.ShowInfo("Equalizer APO was not found yet",
                        "Complete the Equalizer APO installer and enable the APO for your output device with its Configurator, then use Recheck in Settings.",
                        AppInfo.EqualizerApoDocsUrl);
                }
                break;
        }

        _settings.Application.HasSeenWelcome = true;
        _autoSaver.Schedule();
    }

    public void NotifyMinimizedToTray()
    {
        if (!_settings.Application.ShowTrayNotifications) return;
        _tray.ShowNotification("Boost Studio is still running",
            "Processing continues in the background. Double-click the tray icon to reopen.");
    }

    public void RequestExit()
    {
        if (_exitRequested || _disposed) return;
        _exitRequested = true;
        _autoSaver.Flush();
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Navigate(string key)
    {
        var item = NavigationItems.FirstOrDefault(i => i.Key == key);
        if (item is not null)
            SelectedNavigation = item;
    }

    private object PageForKey(string key) => key switch
    {
        "bass" => Bass,
        "volume" => Volume,
        "equalizer" => Equalizer,
        "settings" => Settings,
        _ => Dashboard
    };

    private void SetProcessingEnabled(bool value, bool notify = false)
    {
        if (_settings.ProcessingEnabled == value) return;
        _settings.ProcessingEnabled = value;
        OnPropertyChanged(nameof(ProcessingEnabled), nameof(MasterStatusText), nameof(MasterDescription));
        OnSettingsMutated();
        Dashboard.Refresh();
        if (notify)
            NotifyToggle(value ? "Master processing on" : "Master processing bypassed");
    }

    private void ToggleProcessingFromShell()
    {
        ProcessingEnabled = !ProcessingEnabled;
        NotifyToggle(_settings.ProcessingEnabled ? "Processing enabled" : "Processing bypassed");
    }

    private void ToggleBassFromShell()
    {
        Bass.Enabled = !Bass.Enabled;
        NotifyToggle(Bass.Enabled ? "Bass Booster on" : "Bass Booster off");
    }

    private void ToggleVolumeFromShell()
    {
        Volume.Enabled = !Volume.Enabled;
        NotifyToggle(Volume.Enabled ? "Volume Booster on" : "Volume Booster off");
    }

    private void ToggleEqualizerFromShell()
    {
        Equalizer.Enabled = !Equalizer.Enabled;
        NotifyToggle(Equalizer.Enabled ? "Equalizer on" : "Equalizer off");
    }

    private void NotifyToggle(string message)
    {
        if (!_settings.Application.ShowTrayNotifications) return;
        if (Application.Current?.MainWindow is { IsVisible: true, WindowState: not WindowState.Minimized })
            return;
        _tray.ShowNotification("Boost Studio", message);
    }

    private void OnSettingsMutated()
    {
        if (_disposed) return;
        _settings.Normalize();
        _autoSaver.Schedule();
        _engine.ScheduleApply(_settings, _autoGain.TrimDb);
        SyncRuntimeState();
        Dashboard.Refresh();
        UpdateTrayState();
    }

    private void SyncRuntimeState()
    {
        Monitor.WarningsEnabled = _settings.Application.ShowClipWarnings;
        var agcShouldRun = _settings.ProcessingEnabled && _settings.Volume.Enabled && _settings.Volume.AutoGainControl;
        _autoGain.Configure(agcShouldRun);
        Volume.UpdateAgcStatus();
        Volume.UpdateRuntimeTrim();
        OnPropertyChanged(nameof(ProcessingEnabled), nameof(MasterStatusText), nameof(MasterDescription),
            nameof(EngineSummary), nameof(VersionText));
    }

    private void UpdateTrayState()
    {
        try
        {
            _tray.UpdateState(_settings, _engine.Status.StateLabel);
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not update the tray state: {ex.Message}");
        }
    }

    private void ReloadAllFromSettings()
    {
        _settings.Normalize();
        Bass.ReloadFromSettings();
        Volume.ReloadFromSettings();
        Equalizer.ReplaceLibrary(_presets);
        Equalizer.ReloadFromSettings();
        Settings.ReloadFromSettings();
        SyncRuntimeState();
        _autoSaver.Schedule();
        _engine.ScheduleApply(_settings, _autoGain.TrimDb);
        ApplyHotkeysNow();
        ScheduleMonitorRestart();
        Dashboard.Refresh();
        UpdateTrayState();
    }

    private void ApplyHotkeysNow()
    {
        if (_disposed) return;

        if (_windowHandle == IntPtr.Zero)
        {
            Settings.SetHotkeyStatus("Shortcuts will be registered after the window is ready.", false);
            return;
        }

        if (!_settings.Hotkeys.Enabled)
        {
            _hotkeys.UnregisterAll();
            Settings.SetHotkeyStatus("Global shortcuts are turned off.", false);
            return;
        }

        var result = _hotkeys.Register(_settings.Hotkeys.ToggleBass, _settings.Hotkeys.ToggleVolume,
            _settings.Hotkeys.ToggleProcessing);
        if (result.Success)
        {
            Settings.SetHotkeyStatus(
                $"Registered {Settings.ToggleBassHotkey}, {Settings.ToggleVolumeHotkey} and {Settings.ToggleProcessingHotkey}.",
                false);
        }
        else
        {
            Settings.SetHotkeyStatus(string.Join(" ", result.Errors), true);
            _log.Warn("Global shortcut registration failed: " + string.Join(" ", result.Errors));
        }
    }

    private void OnHotkeyPressed(object? sender, HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.ToggleBass:
                ToggleBassFromShell();
                break;
            case HotkeyAction.ToggleVolume:
                ToggleVolumeFromShell();
                break;
            case HotkeyAction.ToggleProcessing:
                ToggleProcessingFromShell();
                break;
        }
    }

    private void OnAutoGainTrimChanged(object? sender, double trim)
    {
        var dispatcher = Application.Current?.Dispatcher;
        void Apply()
        {
            if (_disposed) return;
            Volume.UpdateRuntimeTrim();
            _engine.ScheduleApply(_settings, trim);
            Dashboard.Refresh();
        }

        if (dispatcher is null || dispatcher.CheckAccess())
            Apply();
        else
            dispatcher.BeginInvoke(Apply, DispatcherPriority.Background);
    }

    private void OnEngineStatusChanged(object? sender, EngineStatus status)
    {
        var dispatcher = Application.Current?.Dispatcher;
        void Apply()
        {
            if (_disposed) return;
            UpdateTrayState();
            Dashboard.Refresh();
        }

        if (dispatcher is null || dispatcher.CheckAccess())
            Apply();
        else
            dispatcher.BeginInvoke(Apply, DispatcherPriority.Background);
    }

    private void OnFrameReady(object? sender, SpectrumFrame frame)
    {
        if (_disposed) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        _latestFrame = frame;
        if (Interlocked.CompareExchange(ref _frameDispatchPending, 1, 0) == 0)
            dispatcher.BeginInvoke(ApplyLatestFrame, DispatcherPriority.Background);
    }

    private void ApplyLatestFrame()
    {
        Interlocked.Exchange(ref _frameDispatchPending, 0);
        var frame = _latestFrame;
        if (frame is null || _disposed) return;

        Monitor.Update(frame);
        if (_settings.ProcessingEnabled && _settings.Volume.Enabled && _settings.Volume.AutoGainControl)
            _autoGain.ProcessPeak(frame.PeakDb, frame.Timestamp);

        if (frame.IsClipping && _settings.Application.ShowClipWarnings)
            MaybeNotifyClipping();
    }

    private void MaybeNotifyClipping()
    {
        if (!_settings.Application.ShowTrayNotifications) return;
        var now = DateTime.UtcNow;
        if (now - _lastClipNotificationUtc < ClipNotificationInterval) return;
        _lastClipNotificationUtc = now;
        _tray.ShowNotification("Output is clipping",
            "The measured output reached full scale. Reduce Volume Booster gain, bass intensity, or EQ boost.");
    }

    private void OnMonitorStopped(object? sender, string message)
    {
        var dispatcher = Application.Current?.Dispatcher;
        void Apply()
        {
            if (_disposed) return;
            Monitor.SetStopped($"Audio monitoring stopped · {message}");
            _log.Warn($"Audio monitoring stopped: {message}");
            ScheduleMonitorRestart(TimeSpan.FromSeconds(3));
        }

        if (dispatcher is null || dispatcher.CheckAccess())
            Apply();
        else
            dispatcher.BeginInvoke(Apply, DispatcherPriority.Background);
    }

    private void ScheduleMonitorRestart()
    {
        ScheduleMonitorRestart(TimeSpan.FromMilliseconds(350));
    }

    private void ScheduleMonitorRestart(TimeSpan delay)
    {
        if (_disposed) return;
        _monitorRestartTimer.Stop();
        _monitorRestartTimer.Interval = delay;
        _monitorRestartTimer.Start();
    }

    private void OnMonitorRestartTimerTick(object? sender, EventArgs e)
    {
        _monitorRestartTimer.Stop();
        _ = RestartMonitorAsync();
    }

    private async Task RestartMonitorAsync()
    {
        if (_disposed) return;
        var generation = Interlocked.Increment(ref _monitorGeneration);
        await _monitorGate.WaitAsync();
        try
        {
            if (_disposed) return;
            if (generation != _monitorGeneration) return;

            Monitor.SetStarting("Connecting to the Windows audio engine…");
            var deviceId = _settings.Audio.MonitorDefaultDevice ? null : _settings.Audio.MonitoringDeviceId;
            var result = await Task.Run(() =>
            {
                var error = _monitorService.Start(deviceId);
                return (Error: error, DeviceName: _monitorService.DeviceName,
                    SampleRate: _monitorService.SampleRate, Channels: _monitorService.Channels,
                    DeviceId: _monitorService.DeviceId);
            });

            if (_disposed) return;
            if (!string.IsNullOrEmpty(result.Error))
            {
                Monitor.SetStopped($"Audio monitoring unavailable · {result.Error}");
                _log.Warn($"System audio monitoring could not start: {result.Error}");
                ScheduleMonitorRestart(TimeSpan.FromSeconds(8));
                return;
            }

            Monitor.SetMonitoring(result.DeviceName, result.SampleRate, result.Channels,
                "Monitoring real system output (post-mix loopback)");
            _lastDefaultDeviceId = _audioDevices.GetDefaultDeviceId();
            _log.Info($"Monitoring {result.DeviceName} ({result.SampleRate} Hz, {result.Channels} ch).");
        }
        catch (Exception ex)
        {
            _log.Error("Could not start system audio monitoring.", ex);
            Monitor.SetStopped($"Audio monitoring unavailable · {ex.Message}");
            ScheduleMonitorRestart(TimeSpan.FromSeconds(8));
        }
        finally
        {
            _monitorGate.Release();
        }
    }

    private void OnDevicePollTick(object? sender, EventArgs e)
    {
        if (_disposed) return;
        _devicePollTicks++;
        try
        {
            var currentDefault = _audioDevices.GetDefaultDeviceId();
            var defaultChanged = !string.Equals(currentDefault, _lastDefaultDeviceId, StringComparison.OrdinalIgnoreCase);
            if (defaultChanged)
            {
                _lastDefaultDeviceId = currentDefault;
                Settings.RefreshDevicesSilently();
                if (_settings.Audio.MonitorDefaultDevice)
                    ScheduleMonitorRestart();
            }
            else if (_devicePollTicks % 10 == 0)
            {
                Settings.RefreshDevicesSilently();
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Device polling failed: {ex.Message}");
        }
    }

    private void RestartElevatedAndExit()
    {
        var (success, error) = _shell.RestartElevated();
        if (!success)
        {
            _dialogs.ShowError("Could not restart as administrator",
                "Boost Studio could not start an elevated instance.", error);
            return;
        }

        _log.Info("Restarting elevated at the user's request.");
        RequestExit();
    }

    private void SaveSettings()
    {
        try
        {
            _settings.Normalize();
            _settingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            _log.Error("Could not save Boost Studio settings.", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _autoSaver.Flush();
        _devicePollTimer.Stop();
        _monitorRestartTimer.Stop();
        _devicePollTimer.Tick -= OnDevicePollTick;
        _monitorRestartTimer.Tick -= OnMonitorRestartTimerTick;
        _monitorService.FrameReady -= OnFrameReady;
        _monitorService.MonitorStopped -= OnMonitorStopped;
        _engine.StatusChanged -= OnEngineStatusChanged;
        _autoGain.TrimChanged -= OnAutoGainTrimChanged;
        _hotkeys.Pressed -= OnHotkeyPressed;
        _hotkeys.Dispose();
        _monitorService.Dispose();
        _testToneService.Dispose();
        TestTone.Dispose();
        Monitor.Dispose();
        Engine.Dispose();
        _tray.Dispose();
        _engine.Dispose();
        _autoSaver.Dispose();
        _log.Info("Boost Studio shut down.");
    }
}
