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

    private Mutex? _singleInstance;
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
            MessageBox.Show(
                "Boost Studio is already running. Open it from the notification area (system tray).",
                "Boost Studio",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        AppPaths.EnsureCreated();
        _log = new FileLogService();
        _log.Info($"Boost Studio {AppInfo.Version} starting. OS: {Environment.OSVersion.VersionString}.");

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
            if (!_viewModel.StartMinimizedRequested)
                window.Show();

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

        if (_singleInstance is not null)
        {
            try { _singleInstance.ReleaseMutex(); } catch { /* ignore */ }
            _singleInstance.Dispose();
            _singleInstance = null;
        }

        base.OnExit(e);
    }
}
