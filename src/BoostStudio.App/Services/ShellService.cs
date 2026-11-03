using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;

namespace BoostStudio.App.Services;

public sealed class ShellService
{
    public bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    public string ExecutablePath
    {
        get
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                return path;

            var assemblyPath = Environment.GetCommandLineArgs().FirstOrDefault();
            return string.IsNullOrWhiteSpace(assemblyPath)
                ? Path.Combine(AppContext.BaseDirectory, "BoostStudio.exe")
                : assemblyPath;
        }
    }

    public bool OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool OpenFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool OpenFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool Launch(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool OpenWindowsSoundSettings() => OpenUrl("ms-settings:sound");

    public (bool Success, string? Error) RestartElevated()
    {
        try
        {
            var executable = ExecutablePath;
            if (!File.Exists(executable))
                return (false, "Could not determine the Boost Studio executable path.");

            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = "--elevated",
                WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory
            };
            Process.Start(startInfo);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Runs the one-time elevated ACL helper and waits for it. The helper grants the
    /// current (non-elevated) user access to the Equalizer APO config files, so the
    /// main UI never has to run elevated for routine adjustments.
    /// </summary>
    public (bool Success, string? Error) GrantEapoAccess(string configDirectory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(configDirectory) || !Directory.Exists(configDirectory))
                return (false, "The Equalizer APO configuration folder was not found.");

            string? sid;
            using (var identity = WindowsIdentity.GetCurrent())
                sid = identity.User?.Value;

            if (string.IsNullOrWhiteSpace(sid))
                return (false, "The current Windows user could not be identified.");

            var executable = ExecutablePath;
            if (!File.Exists(executable))
                return (false, "Could not determine the Boost Studio executable path.");

            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"--grant-eapo-access \"{configDirectory}\" \"{sid}\"",
                WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory
            };

            using var process = Process.Start(startInfo);
            if (process is null)
                return (false, "The elevated helper could not be started.");

            if (!process.WaitForExit(120_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return (false, "The elevated helper did not finish within two minutes.");
            }

            return process.ExitCode == 0
                ? (true, null)
                : (false, $"The elevated helper exited with code {process.ExitCode}. See the Boost Studio log for details.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, "The Windows permission prompt was cancelled.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public bool TryOpenLogs(string logsDirectory) => OpenFolder(logsDirectory);

    public void ShutdownApplication() => Application.Current?.Shutdown();
}
