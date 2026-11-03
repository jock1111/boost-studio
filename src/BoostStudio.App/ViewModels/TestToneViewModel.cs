using System.Windows;
using System.Windows.Threading;
using BoostStudio.App.Infrastructure;
using BoostStudio.App.Services;
using BoostStudio.Core.Audio;

namespace BoostStudio.App.ViewModels;

/// <summary>Shared test-tone controls used on the Bass, Volume and Settings pages.</summary>
public sealed class TestToneViewModel : ObservableObject, IDisposable
{
    private readonly TestToneService _service;
    private bool _isPlaying;
    private string _statusText = "Play a real tone through Windows to hear the current chain.";
    private bool _disposed;

    public TestToneViewModel(TestToneService service)
    {
        _service = service;
        _service.PlaybackEnded += OnPlaybackEnded;
        PlayBassCommand = new RelayCommand(() => Play(TestToneKind.Bass, "60 Hz bass test tone"));
        PlayLevelCommand = new RelayCommand(() => Play(TestToneKind.Level, "1 kHz level test tone"));
        PlaySweepCommand = new RelayCommand(() => Play(TestToneKind.Sweep, "30 Hz – 18 kHz sweep"));
        StopCommand = new RelayCommand(Stop, () => IsPlaying);
    }

    public RelayCommand PlayBassCommand { get; }
    public RelayCommand PlayLevelCommand { get; }
    public RelayCommand PlaySweepCommand { get; }
    public RelayCommand StopCommand { get; }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (!SetProperty(ref _isPlaying, value)) return;
            StopCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(PlayButtonText));
        }
    }

    public string PlayButtonText => IsPlaying ? "Stop test tone" : "Play test tone";

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    private void Play(TestToneKind kind, string label)
    {
        if (IsPlaying)
        {
            Stop();
            return;
        }

        var (success, error) = _service.Play(kind);
        if (success)
        {
            IsPlaying = true;
            StatusText = $"Playing {label} through the Windows shared-mode output.";
        }
        else
        {
            IsPlaying = false;
            StatusText = error ?? "The test tone could not be played.";
        }
    }

    public void Stop()
    {
        _service.Stop();
        IsPlaying = false;
        StatusText = "Test tone stopped.";
    }

    private void OnPlaybackEnded(object? sender, EventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            Finished();
        else
            dispatcher.BeginInvoke(Finished, DispatcherPriority.Background);
    }

    private void Finished()
    {
        IsPlaying = false;
        StatusText = "Test tone finished.";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _service.PlaybackEnded -= OnPlaybackEnded;
    }
}
