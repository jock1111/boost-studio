using System.Text;
using BoostStudio.Core.Models;

namespace BoostStudio.Core.Storage;

public interface ILogService
{
    void Info(string message);

    void Warn(string message);

    void Error(string message, Exception? exception = null);

    string LogPath { get; }
}

public sealed class FileLogService : ILogService
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private readonly object _gate = new();

    public string LogPath { get; }

    public FileLogService(string? logPath = null)
    {
        LogPath = logPath ?? AppPaths.LogFile;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        }
        catch
        {
            // Logging must never prevent the application from starting.
        }
    }

    public void Info(string message) => Write("INFO", message, null);

    public void Warn(string message) => Write("WARN", message, null);

    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        var line = new StringBuilder()
            .Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append("Z [").Append(level).Append("] ")
            .Append(message);

        if (exception is not null)
        {
            line.AppendLine();
            line.Append("    ").Append(exception.GetType().Name).Append(": ").Append(exception.Message);
            if (!string.IsNullOrWhiteSpace(exception.StackTrace))
                line.AppendLine().Append(exception.StackTrace);
        }

        line.AppendLine();

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                RollIfNeeded();
                File.AppendAllText(LogPath, line.ToString(), Encoding.UTF8);
            }
            catch
            {
                // Ignore logging failures.
            }
        }
    }

    private void RollIfNeeded()
    {
        try
        {
            var info = new FileInfo(LogPath);
            if (!info.Exists || info.Length < MaxBytes)
                return;

            var previous = Path.Combine(info.DirectoryName!, "booststudio.previous.log");
            if (File.Exists(previous))
                File.Delete(previous);
            File.Move(LogPath, previous);
        }
        catch
        {
            // Ignore.
        }
    }
}
