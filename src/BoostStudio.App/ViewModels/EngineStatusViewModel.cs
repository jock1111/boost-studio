using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using BoostStudio.App.Infrastructure;
using BoostStudio.App.Services;
using BoostStudio.Core.Audio;
using BoostStudio.Core.Dsp;
using BoostStudio.Core.Engine;
using BoostStudio.Core.Models;
using BoostStudio.Core.Storage;

namespace BoostStudio.App.ViewModels;

public sealed class EngineStatusViewModel : ObservableObject, IDisposable
{
    private static readonly Brush ReadyBrush = Freeze(Color.FromRgb(53, 224, 195));
    private static readonly Brush WarningBrush = Freeze(Color.FromRgb(245, 201, 93));
    private static readonly Brush DangerBrush = Freeze(Color.FromRgb(249, 112, 102));
    private static readonly Brush InfoBrush = Freeze(Color.FromRgb(125, 211, 252));
    private static readonly Brush MutedBrush = Freeze(Color.FromRgb(130, 148, 165));

    private readonly AudioEngineService _engine;
    private readonly AudioDeviceService _audioDevices;
    private readonly AppSettings _settings;
    private readonly IDialogService _dialogs;
    private readonly ShellService _shell;
    private readonly ILogService _log;
    private readonly Func<double> _trimProvider;
    private readonly Action _restartElevated;
    private readonly List<AsyncRelayCommand> _commands = new();
    private EngineStatus _status = EngineStatus.Detecting;
    private bool _isBusy;
    private bool _disposed;

    public EngineStatusViewModel(
        AudioEngineService engine,
        AppSettings settings,
        IDialogService dialogs,
        ShellService shell,
        ILogService log,
        Func<double> trimProvider,
        Action restartElevated)
    {
        _engine = engine;
        _audioDevices = new AudioDeviceService(log);
        _settings = settings;
        _dialogs = dialogs;
        _shell = shell;
        _log = log;
        _trimProvider = trimProvider;
        _restartElevated = restartElevated;

        RefreshCommand = CreateCommand(RefreshAsync);
        ConnectCommand = CreateCommand(ConnectAsync);
        UseSuggestedApoOutputCommand = CreateCommand(UseSuggestedApoOutputAsync);
        DisconnectCommand = CreateCommand(DisconnectAsync);
        RestoreBaselineCommand = CreateCommand(RestoreBaselineAsync);
        InstallCommand = CreateCommand(InstallAsync);
        GrantAccessCommand = new AsyncRelayCommand(GrantAccessAsync, () => CanGrantAccess, OnCommandError);
        OpenConfigFolderCommand = new RelayCommand(OpenConfigFolder);
        OpenEditorCommand = new RelayCommand(OpenEditor);
        OpenDeviceSelectorCommand = new RelayCommand(OpenDeviceSelector);
        PreviewConfigCommand = new RelayCommand(PreviewConfig);
        OpenLogCommand = new RelayCommand(OpenLog);
        RestartElevatedCommand = new RelayCommand(RestartElevated);
        OpenConfigFileCommand = new RelayCommand(OpenConfigFile);

        _engine.StatusChanged += OnStatusChanged;
        UpdateStatus(_engine.Status);
    }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand UseSuggestedApoOutputCommand { get; }
    public AsyncRelayCommand DisconnectCommand { get; }
    public AsyncRelayCommand RestoreBaselineCommand { get; }
    public AsyncRelayCommand InstallCommand { get; }
    public AsyncRelayCommand GrantAccessCommand { get; }
    public RelayCommand OpenConfigFolderCommand { get; }
    public RelayCommand OpenEditorCommand { get; }
    public RelayCommand OpenDeviceSelectorCommand { get; }
    public RelayCommand PreviewConfigCommand { get; }
    public RelayCommand OpenLogCommand { get; }
    public RelayCommand RestartElevatedCommand { get; }
    public RelayCommand OpenConfigFileCommand { get; }

    public EngineStatus Status => _status;

    public string StateLabel => _status.StateLabel;
    public string Summary => _status.Summary;
    public string? Detail => string.IsNullOrWhiteSpace(_status.Detail) ? null : _status.Detail;
    public bool HasDetail => Detail is not null;
    public bool IsInstalled => _status.Installed;
    public bool IsReady => _status.IsReady;
    public bool IsIntegrated => _status.IntegrationActive;
    public bool IsBypassed => _status.State == EngineState.Bypassed;
    public bool ProcessingVerified => _status.ProcessingVerified;
    public bool IsMissing => _status.State == EngineState.NotInstalled;
    public bool NeedsPermission => _status.State == EngineState.PermissionDenied;
    public bool IsApoNotEnabled => _status.State == EngineState.ApoNotEnabled;
    public bool NeedsApoSetup => _status.NeedsApoSetup;
    public bool NeedsConnection => _status.Installed && !_status.IntegrationActive;
    public bool IsElevated => _shell.IsElevated;

    public bool CanGrantAccess => IsInstalled && !_status.ConfigWritable && !IsBusy;
    public string ElevationText => IsElevated ? "Running as administrator" : "Standard user session";

    public string ApoStatusText
    {
        get
        {
            if (!_status.Installed)
                return "Equalizer APO not installed";
            if (!_status.ApoStatusChecked)
                return "APO association not checked";
            if (_status.TargetApoEnabled)
                return string.IsNullOrWhiteSpace(_status.ProcessingScope)
                    ? "APO registered on the target output"
                    : $"APO registered on {_status.ProcessingScope}";
            return "APO not registered on the target output";
        }
    }

    public string ApoDeviceCountText
    {
        get
        {
            if (!_status.ApoStatusChecked)
                return "Device APO association could not be read.";
            if (!_status.TargetApoEnabled && _status.ApoEnabledDeviceCount > 0)
            {
                return _status.ApoEnabledDeviceCount == 1
                    ? "1 output has Equalizer APO enabled, but not the target output."
                    : $"{_status.ApoEnabledDeviceCount} outputs have Equalizer APO enabled, but not the target output.";
            }

            return _status.ApoEnabledDeviceCount switch
            {
                0 => "No active output has Equalizer APO enabled.",
                1 => "1 active output has Equalizer APO enabled.",
                _ => $"{_status.ApoEnabledDeviceCount} active outputs have Equalizer APO enabled."
            };
        }
    }

    public string VerificationText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_status.VerificationText))
                return _status.VerificationText!;

            if (_status.ProcessingVerified)
            {
                var delta = _status.MeasuredPreampDeltaDb is { } measured
                    ? $" The -18 dB test measured {measured:+0.0;-0.0;0.0} dB."
                    : string.Empty;
                return $"Processing verified by WASAPI loopback.{delta}";
            }

            return _status.State switch
            {
                EngineState.NotInstalled => "Not verified: Equalizer APO is not installed.",
                EngineState.PermissionDenied => "Not verified: the configuration was not written.",
                EngineState.NotIntegrated => "Not verified: no active configuration.",
                EngineState.ApoNotEnabled => "Configuration written; audible processing was not verified.",
                _ => "Configuration written; audible processing was not measured. Run Test audio engine in Settings."
            };
        }
    }

    public bool HasSuggestedApoOutput => IsApoNotEnabled &&
                                         !string.IsNullOrWhiteSpace(_status.SuggestedApoDeviceId) &&
                                         !string.IsNullOrWhiteSpace(_status.SuggestedApoDeviceName);

    public string SuggestedApoOutputText => HasSuggestedApoOutput
        ? $"Equalizer APO is enabled on “{_status.SuggestedApoDeviceName}”, but not on the current Windows default output."
        : string.Empty;

    public string LastVerifiedText => _status.LastVerifiedUtc is { } verified
        ? $"Measured {verified.ToLocalTime():HH:mm:ss}"
        : "Not measured this session";

    public string VersionText => string.IsNullOrWhiteSpace(_status.Version)
        ? "Version unknown"
        : $"Equalizer APO {_status.Version}";

    public string InstallPath => _status.InstallDirectory ?? "Not detected";
    public string ConfigPath => _status.ConfigDirectory ?? "Not detected";

    public string ConfigWriteText => !_status.Installed
        ? "Not available"
        : _status.ConfigWritable ? "Config folder is writable" : "Config folder needs permission";

    public string FilterSummary => _status.ActiveFilterCount == 1
        ? "1 active filter"
        : $"{_status.ActiveFilterCount} active filters";

    public string PreampText => _status.PreampDb.ToString("+0.0;-0.0;0.0") + " dB";
    public string HeadroomText => _status.HeadroomReservedDb > 0.05
        ? $"−{_status.HeadroomReservedDb:0.0} dB reserved"
        : "No headroom reserved";

    public string LastAppliedText => _status.LastAppliedUtc is { } applied
        ? $"Applied {applied.ToLocalTime():HH:mm:ss}"
        : "Not applied in this session";

    public string ConnectionDetail => _status.IntegrationActive
        ? "Managed include is present in config.txt"
        : "No Boost Studio include in config.txt";

    public string EngineActionText => _status.State switch
    {
        EngineState.NotInstalled => "Install Equalizer APO",
        EngineState.NotIntegrated => "Connect",
        EngineState.PermissionDenied => "Grant access",
        EngineState.ApoNotEnabled => "Recheck device setup",
        EngineState.Ready => "Repair configuration",
        EngineState.Bypassed => "Repair configuration",
        EngineState.Error => "Retry",
        _ => "Refresh"
    };

    public Brush StateBrush => _status.State switch
    {
        EngineState.Ready when _status.ProcessingVerified => ReadyBrush,
        EngineState.Ready => WarningBrush,
        EngineState.Bypassed => WarningBrush,
        EngineState.NotInstalled => DangerBrush,
        EngineState.PermissionDenied => WarningBrush,
        EngineState.ApoNotEnabled => WarningBrush,
        EngineState.Error => DangerBrush,
        EngineState.NotIntegrated => InfoBrush,
        _ => MutedBrush
    };

    public Brush StateBackgroundBrush
    {
        get
        {
            var color = ((SolidColorBrush)StateBrush).Color;
            var brush = new SolidColorBrush(Color.FromArgb(28, color.R, color.G, color.B));
            brush.Freeze();
            return brush;
        }
    }

    public string StatusGlyph => _status.State switch
    {
        EngineState.Ready when _status.ProcessingVerified => "\uE73E",
        EngineState.Ready => "\uE946",
        EngineState.Bypassed => "\uE7BA",
        EngineState.NotInstalled => "\uE7BA",
        EngineState.PermissionDenied => "\uE72E",
        EngineState.ApoNotEnabled => "\uE7BA",
        EngineState.Error => "\uEA39",
        EngineState.NotIntegrated => "\uE946",
        _ => "\uE946"
    };

    public string StateHeader => _status.State switch
    {
        EngineState.Ready when _status.ProcessingVerified => "DSP processing verified by measured loopback",
        EngineState.Ready => "DSP configuration written; processing not measured",
        EngineState.Bypassed => "Engine connected, audio bypassed",
        EngineState.NotInstalled => "Equalizer APO is required",
        EngineState.NotIntegrated => "Ready to connect",
        EngineState.PermissionDenied => "Administrator permission required",
        EngineState.ApoNotEnabled => "Configuration written, but the APO is not enabled",
        EngineState.Error => "The audio engine needs attention",
        _ => "Detecting Equalizer APO"
    };

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            RefreshCommand.RaiseCanExecuteChanged();
            ConnectCommand.RaiseCanExecuteChanged();
            DisconnectCommand.RaiseCanExecuteChanged();
            RestoreBaselineCommand.RaiseCanExecuteChanged();
            InstallCommand.RaiseCanExecuteChanged();
            GrantAccessCommand.RaiseCanExecuteChanged();
            UseSuggestedApoOutputCommand.RaiseCanExecuteChanged();
        }
    }

    public bool CanConnect => IsInstalled && !IsBusy;
    public bool CanDisconnect => IsIntegrated && !IsBusy;
    public bool CanRestoreBaseline => IsInstalled && !IsBusy;
    public bool CanOpenEditor => _engine.CurrentInstallation?.EditorPath is not null;
    public bool CanOpenDeviceSelector => _engine.CurrentInstallation?.DeviceSelectorPath is not null;

    private AsyncRelayCommand CreateCommand(Func<Task> action)
    {
        var command = new AsyncRelayCommand(async () => await RunAsync(action), () => !IsBusy, OnCommandError);
        _commands.Add(command);
        return command;
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await action();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnCommandError(Exception ex)
    {
        _log.Error("An engine command failed.", ex);
        _dialogs.ShowError("Engine command failed", ex.Message);
    }

    private void OnStatusChanged(object? sender, EngineStatus status)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            UpdateStatus(status);
        else
            dispatcher.BeginInvoke(() => UpdateStatus(status), DispatcherPriority.Background);
    }

    private void UpdateStatus(EngineStatus status)
    {
        _status = status;
        OnPropertyChanged(nameof(Status), nameof(StateLabel), nameof(Summary), nameof(Detail),
            nameof(HasDetail), nameof(IsInstalled), nameof(IsReady), nameof(IsIntegrated),
            nameof(IsBypassed), nameof(IsMissing), nameof(NeedsPermission), nameof(NeedsConnection),
            nameof(IsApoNotEnabled), nameof(NeedsApoSetup), nameof(ApoStatusText),
            nameof(ApoDeviceCountText), nameof(VerificationText), nameof(ProcessingVerified),
            nameof(HasSuggestedApoOutput), nameof(SuggestedApoOutputText),
            nameof(LastVerifiedText), nameof(CanGrantAccess),
            nameof(VersionText), nameof(InstallPath), nameof(ConfigPath), nameof(ConfigWriteText),
            nameof(FilterSummary), nameof(PreampText), nameof(HeadroomText), nameof(LastAppliedText),
            nameof(ConnectionDetail), nameof(EngineActionText), nameof(StateBrush),
            nameof(StateBackgroundBrush), nameof(StatusGlyph), nameof(StateHeader),
            nameof(CanConnect), nameof(CanDisconnect), nameof(CanRestoreBaseline),
            nameof(CanOpenEditor), nameof(CanOpenDeviceSelector));
    }

    private async Task RefreshAsync()
    {
        var status = await _engine.RefreshAsync(_settings);
        UpdateStatus(status);
    }

    private async Task ConnectAsync()
    {
        if (!_settings.AnyProcessingEnabled)
        {
            _dialogs.ShowInfo("Nothing to connect",
                "Enable Bass Booster, Volume Booster or the Equalizer first, then connect the engine.");
            return;
        }

        var status = await _engine.ReconnectAsync(_settings, _trimProvider());
        UpdateStatus(status);
        if (status.State == EngineState.Error)
            _dialogs.ShowError("Could not connect the audio engine", status.Summary, status.Detail);
    }

    /// <summary>
    /// Switches the Windows default output to an endpoint that already has Equalizer
    /// APO enabled. This is offered only when the current default output is unprocessed,
    /// and it always asks before changing audio routing.
    /// </summary>
    private async Task UseSuggestedApoOutputAsync()
    {
        if (!HasSuggestedApoOutput) return;

        var id = _status.SuggestedApoDeviceId!;
        var name = _status.SuggestedApoDeviceName!;
        if (!_dialogs.Confirm("Switch Windows default output?",
                $"Make “{name}” the Windows default output? Equalizer APO is already enabled there, so Boost Studio processing can reach your audio. Sound will move to that device immediately.",
                "Switch output", "Cancel"))
            return;

        var result = _audioDevices.TrySetDefaultDevice(id);
        if (!result.Success)
        {
            _dialogs.ShowWarning("Could not switch the default output", result.Message,
                "Windows Settings → System → Sound → Output lets you choose the default device.");
            return;
        }

        // Let the audio policy service settle before re-enumerating endpoints.
        await Task.Delay(350);
        var status = await _engine.RefreshAsync(_settings);
        UpdateStatus(status);

        if (status.TargetApoEnabled)
        {
            _dialogs.ShowSuccess("Default output switched",
                $"Windows now sends audio to “{name}”, where Equalizer APO is enabled.",
                "Run “Test audio engine” to measure that processing reaches the output.");
        }
        else
        {
            _dialogs.ShowWarning("Output switched, but the APO is still not detected",
                "Equalizer APO may need a moment, or the output may need to be enabled in its Device Selector.",
                name);
        }
    }

    private async Task DisconnectAsync()
    {
        if (!_dialogs.Confirm("Disconnect audio engine?",
                "Boost Studio will remove its managed Include line and DSP file from Equalizer APO. Your own Equalizer APO configuration is preserved, but Boost Studio processing will stop until you reconnect.",
                "Disconnect", "Cancel", danger: true))
            return;

        var status = await _engine.RemoveIntegrationAsync(_settings);
        UpdateStatus(status);
        if (status.State == EngineState.Error)
            _dialogs.ShowError("Could not disconnect", status.Summary, status.Detail);
        else
            _dialogs.ShowSuccess("Engine disconnected", status.Summary, status.Detail);
    }

    private async Task RestoreBaselineAsync()
    {
        var store = _engine.TryGetConfigStore(_settings);
        var baselineDiffers = store?.BaselineDiffersFromCurrent == true;
        var message = baselineDiffers
            ? "You have edited Equalizer APO's config.txt since Boost Studio saved its baseline. Restoring the baseline will replace those later edits. The current file is backed up first, and Boost Studio will disconnect."
            : "The configuration captured before Boost Studio first connected will replace config.txt. The current configuration is backed up first, and Boost Studio will disconnect.";

        if (!_dialogs.Confirm("Restore your original Equalizer APO configuration?", message,
                "Restore baseline", "Cancel", danger: true))
            return;

        var status = await _engine.RestoreBaselineAsync(_settings);
        UpdateStatus(status);
        if (status.State == EngineState.Error)
            _dialogs.ShowError("Baseline restore failed", status.Summary, status.Detail);
        else
            _dialogs.ShowSuccess("Baseline restored", status.Summary, status.Detail);
    }

    private async Task InstallAsync()
    {
        var choice = _dialogs.ShowWelcome(_status);
        switch (choice)
        {
            case WelcomeChoice.Download:
                if (!_shell.OpenUrl(AppInfo.EqualizerApoDownloadUrl))
                    _dialogs.ShowError("Could not open the download page",
                        "Open this address in your browser:", AppInfo.EqualizerApoDownloadUrl);
                break;
            case WelcomeChoice.Recheck:
                var status = await _engine.RefreshAsync(_settings);
                UpdateStatus(status);
                if (status.Installed)
                    await ConnectAsync();
                else
                    _dialogs.ShowInfo("Equalizer APO was not found",
                        "Finish installing Equalizer APO, run its Configurator to enable the APO on your output device, then try again.",
                        AppInfo.EqualizerApoDocsUrl);
                break;
        }
    }

    private void OpenConfigFolder()
    {
        var folder = _status.ConfigDirectory;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            _dialogs.ShowWarning("Config folder not found",
                "Equalizer APO has not been detected yet, or its configuration folder has moved.");
            return;
        }

        if (!_shell.OpenFolder(folder))
            _dialogs.ShowWarning("Could not open the folder", folder);
    }

    private void OpenConfigFile()
    {
        var file = Path.Combine(_status.ConfigDirectory ?? string.Empty, "config.txt");
        if (!File.Exists(file))
        {
            _dialogs.ShowWarning("config.txt was not found", _status.ConfigDirectory ?? "Equalizer APO has not been detected.");
            return;
        }

        if (!_shell.OpenFile(file))
            _dialogs.ShowWarning("Could not open config.txt", file);
    }

    private void OpenEditor()
    {
        var path = _engine.CurrentInstallation?.EditorPath;
        if (path is null || !_shell.Launch(path))
            _dialogs.ShowWarning("Equalizer APO Editor was not found",
                "Open the Equalizer APO installation folder and launch its Editor manually.");
    }

    private void OpenDeviceSelector()
    {
        var path = _engine.CurrentInstallation?.DeviceSelectorPath;
        if (path is null || !_shell.Launch(path))
            _dialogs.ShowWarning("APO Device Selector was not found",
                "Open Equalizer APO's Configurator or Device Selector from its installation folder to enable APO on an output device.");
    }

    private void PreviewConfig()
    {
        var config = new DspConfigBuilder().Build(_settings, _trimProvider());
        _dialogs.ShowText("Generated DSP configuration",
            "This is the exact text Boost Studio writes to BoostStudio.txt. Equalizer APO reloads it automatically.",
            config.Text, "Copy configuration");
    }

    private void OpenLog()
    {
        if (!_shell.OpenFile(_log.LogPath))
            _dialogs.ShowInfo("Log file not found yet", _log.LogPath);
    }

    private async Task GrantAccessAsync()
    {
        var directory = _status.ConfigDirectory;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            _dialogs.ShowWarning("Config folder not found",
                "Equalizer APO has not been detected yet, or its configuration folder has moved.");
            return;
        }

        if (!_dialogs.Confirm("Grant Boost Studio access to Equalizer APO?",
                "Windows will ask for administrator approval once. Boost Studio will grant your standard user account Modify access to config.txt and BoostStudio.txt, plus permission to create files in the Equalizer APO config folder. No other folders are changed.",
                "Grant access", "Cancel"))
            return;

        var (success, error) = _shell.GrantEapoAccess(directory);
        if (!success)
        {
            _dialogs.ShowError("Could not grant access",
                "The one-time permission grant did not complete.",
                $"{error}\n\nYou can still use “Restart as administrator” to apply changes for this session.");
            return;
        }

        var status = await _engine.RefreshAsync(_settings);
        UpdateStatus(status);
        _dialogs.ShowSuccess("Access granted",
            "Routine Boost Studio changes can now update Equalizer APO without running the whole UI as administrator.",
            "If Windows or another tool resets the folder permissions, run this grant again.");
    }

    private void RestartElevated()
    {
        if (IsElevated)
        {
            _dialogs.ShowInfo("Already elevated", "Boost Studio is already running with administrator permissions.");
            return;
        }

        if (!_dialogs.Confirm("Restart Boost Studio as administrator?",
                "Windows will ask for permission. Boost Studio will close and reopen with access to Equalizer APO's configuration folder.",
                "Restart", "Cancel"))
            return;

        _restartElevated();
    }

    private static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.StatusChanged -= OnStatusChanged;
    }
}
