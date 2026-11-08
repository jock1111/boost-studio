using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using BoostStudio.App.ViewModels;
using BoostStudio.Core.Engine;
using BoostStudio.Core.Models;
using BoostStudio.Core.Storage;

namespace BoostStudio.App;

public partial class App : Application
{
    private const string SingleInstanceName = @"Local\BoostStudio.App.SingleInstance";
    private const string ShowWindowEventName = @"Local\BoostStudio.App.ShowWindow";

    private Mutex? _singleInstance;
    private EventWaitHandle? _showWindowEvent;
    private RegisteredWaitHandle? _showWindowWait;
    private bool _pendingShow;
    private FileLogService? _log;
    private MainViewModel? _viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (TryRunAccessGrant(e.Args))
            return;

        _singleInstance = new Mutex(true, SingleInstanceName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            // A second launch should reveal the running window instead of showing a
            // dead-end message box. The existing instance listens on a named event.
            if (!SignalExistingInstance())
            {
                MessageBox.Show(
                    "Boost Studio is already running. Open it from the notification area (system tray).",
                    "Boost Studio",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        AppPaths.EnsureCreated();
        _log = new FileLogService();
        _log.Info($"Boost Studio {AppInfo.Version} starting. OS: {Environment.OSVersion.VersionString}.");
        ListenForActivation();

        if (TryRunAudioSelfTest(e.Args))
            return;

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            _viewModel = new MainViewModel(_log, e.Args);
            var window = new MainWindow(_viewModel);
            MainWindow = window;

            // Create the HWND before deciding whether to show it. This attaches the
            // global hotkey hook and lets --minimized start silently in the tray.
            _ = new WindowInteropHelper(window).EnsureHandle();
            if (_pendingShow)
            {
                _pendingShow = false;
                window.ShowFromTray();
            }
            else if (!_viewModel.StartMinimizedRequested)
            {
                window.Show();
            }

            _ = InitializeAsync(_viewModel);
        }
        catch (Exception ex)
        {
            _log.Error("Boost Studio failed during startup.", ex);
            MessageBox.Show(
                "Boost Studio could not start." + Environment.NewLine + Environment.NewLine + ex.Message,
                "Boost Studio",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    /// <summary>
    /// Headless diagnostic entry point: <c>BoostStudio.exe --audio-self-test</c> loads the
    /// saved settings, runs the measured audio path test, writes a report and exits with
    /// 0 (verified), 1 (configuration written but processing not measured) or 2 (error).
    /// </summary>
    private bool TryRunAudioSelfTest(string[] args)
    {
        if (!args.Any(a => string.Equals(a, "--audio-self-test", StringComparison.OrdinalIgnoreCase)))
            return false;

        _log?.Info("Command-line audio self-test starting.");
        var detail = new StringBuilder();
        var exitCode = 2;
        AudioEngineService? engine = null;

        try
        {
            var settings = new SettingsStore(log: _log).Load();
            engine = new AudioEngineService(_log);
            var result = engine.TestAudioProcessingAsync(settings).GetAwaiter().GetResult();
            detail.AppendLine(result.Detail);
            if (!string.IsNullOrWhiteSpace(result.ReportPath))
                detail.AppendLine($"Measured self-test report: {result.ReportPath}");

            exitCode = result.ProcessingVerified && result.GainChangeMeasured ? 0 : 1;
            if (exitCode != 0)
                detail.AppendLine("The DSP file alone is not proof of processing. Read the diagnostic hints in the report, then fix the APO association and retry.");
        }
        catch (Exception ex)
        {
            _log?.Error("Command-line audio self-test failed.", ex);
            detail.AppendLine(ex.ToString());
            exitCode = 2;
        }
        finally
        {
            engine?.Dispose();
        }

        // WinExe has no attached console, so a stable report file is the way to read
        // the result from a terminal or automation.
        try
        {
            Directory.CreateDirectory(AppPaths.DiagnosticsDirectory);
            var cliPath = Path.Combine(AppPaths.DiagnosticsDirectory, "audio-self-test-cli-latest.txt");
            detail.AppendLine();
            detail.AppendLine($"Exit code: {exitCode}");
            File.WriteAllText(cliPath, detail.ToString(), new UTF8Encoding(false));
            _log?.Info($"Command-line audio self-test finished with exit code {exitCode}. Report: {cliPath}");
        }
        catch (Exception ex)
        {
            _log?.Warn($"Could not write the command-line self-test report: {ex.Message}");
        }

        Shutdown(exitCode);
        return true;
    }

    private bool TryRunAccessGrant(string[] args)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, "--grant-eapo-access", StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return false;

        AppPaths.EnsureCreated();
        var log = new FileLogService();
        var configDirectory = index + 1 < args.Length ? args[index + 1] : string.Empty;
        var userSid = index + 2 < args.Length ? args[index + 2] : null;

        log.Info("Elevated Equalizer APO access helper starting.");
        var result = EqualizerApoAccessGrant.Grant(configDirectory, userSid);
        if (result.Success)
            log.Info($"{result.Message} {result.Detail}".Trim());
        else
            log.Error($"Equalizer APO access grant failed: {result.Message} {result.Detail}".Trim());

        // The unelevated parent waits for this exit code and reports the result.
        Shutdown(result.Success ? 0 : 2);
        return true;
    }

    /// <summary>
    /// Waits for activation requests from later launches. The event is auto-reset so
    /// several quick launches coalesce into one show request.
    /// </summary>
    private void ListenForActivation()
    {
        try
        {
            _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
            _showWindowWait = ThreadPool.RegisterWaitForSingleObject(
                _showWindowEvent, OnShowWindowSignaled, null, Timeout.Infinite, executeOnlyOnce: false);
        }
        catch (Exception ex)
        {
            _log?.Warn($"Single-instance activation is unavailable: {ex.Message}");
        }
    }

    private static bool SignalExistingInstance()
    {
        // The first instance creates the event immediately after taking the mutex,
        // so retry briefly to cover a simultaneous cold start.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowWindowEventName, out var handle))
                {
                    using (handle)
                    {
                        handle.Set();
                    }

                    return true;
                }
            }
            catch (Exception)
            {
                // Access denied or the handle is closing; retry.
            }

            Thread.Sleep(50);
        }

        return false;
    }

    private void OnShowWindowSignaled(object? state, bool timedOut)
    {
        if (timedOut) return;

        var dispatcher = Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            return;

        dispatcher.BeginInvoke(() =>
        {
            if (MainWindow is MainWindow window)
            {
                window.ShowFromTray();
            }
            else
            {
                // A very early activation request; show once the window exists.
                _pendingShow = true;
            }
        }, DispatcherPriority.Normal);
    }

    private async Task InitializeAsync(MainViewModel viewModel)
    {
        try
        {
            await viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            _log?.Error("Boost Studio initialization failed.", ex);
            MessageBox.Show(
                "Some Boost Studio features could not start." + Environment.NewLine + Environment.NewLine + ex.Message,
                "Boost Studio",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Error("Unhandled UI exception.", e.Exception);
        e.Handled = true;

        try
        {
            MessageBox.Show(
                "Boost Studio encountered an unexpected problem." + Environment.NewLine + Environment.NewLine +
                e.Exception.Message + Environment.NewLine + Environment.NewLine +
                "The details were written to the application log.",
                "Boost Studio",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch
        {
            // Ignore failures while reporting a failure.
        }
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        _log?.Error("Unhandled application domain exception.",
            e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown error"));
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _log?.Error("Unobserved background task exception.", e.Exception);
        e.SetObserved();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _viewModel?.Dispose();
        }
        catch (Exception ex)
        {
            _log?.Error("Error while shutting down.", ex);
        }

        _log?.Info("Boost Studio exited.");
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;

        // The callback only uses BeginInvoke, so waiting for it cannot deadlock.
        try { _showWindowWait?.Unregister(null); } catch { /* ignore */ }
        _showWindowWait = null;
        _showWindowEvent?.Dispose();
        _showWindowEvent = null;

        if (_singleInstance is not null)
        {
            try { _singleInstance.ReleaseMutex(); } catch { /* ignore */ }
            _singleInstance.Dispose();
            _singleInstance = null;
        }

        base.OnExit(e);
    }
}
