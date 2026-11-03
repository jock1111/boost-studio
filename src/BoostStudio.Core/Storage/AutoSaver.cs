using System.Text;

namespace BoostStudio.Core.Storage;

public sealed class AutoSaver : IDisposable
{
    private readonly TimeSpan _delay;
    private readonly Action _save;
    private readonly ILogService? _log;
    private readonly object _gate = new();
    private Timer? _timer;
    private bool _disposed;
    private int _pending;

    public AutoSaver(Action save, TimeSpan? delay = null, ILogService? log = null)
    {
        _save = save;
        _delay = delay ?? TimeSpan.FromMilliseconds(700);
        _log = log;
    }

    public void Schedule()
    {
        lock (_gate)
        {
            if (_disposed) return;
            Interlocked.Exchange(ref _pending, 1);
            _timer ??= new Timer(OnTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer.Change(_delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnTimer(object? state)
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _pending, 0) == 0) return;

        try
        {
            _save();
        }
        catch (Exception ex)
        {
            _log?.Error("Automatic settings save failed.", ex);
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        if (Interlocked.Exchange(ref _pending, 0) == 0) return;

        try
        {
            _save();
        }
        catch (Exception ex)
        {
            _log?.Error("Saving settings failed.", ex);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }

        Flush();
    }
}
