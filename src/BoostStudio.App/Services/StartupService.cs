using System.IO;
using Microsoft.Win32;

namespace BoostStudio.App.Services;

/// <summary>HKCU Run-key startup entry. Uses the current executable so portable builds work too.</summary>
public sealed class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "BoostStudio";

    private readonly ShellService _shell;

    public StartupService(ShellService shell)
    {
        _shell = shell;
    }

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public string? GetConfiguration()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) as string;
        }
        catch
        {
            return null;
        }
    }

    public void Apply(bool enabled, bool startMinimized)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
                        ?? throw new InvalidOperationException("Could not open the Windows startup registry key.");

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executable = _shell.ExecutablePath;
        if (!File.Exists(executable))
            throw new FileNotFoundException("Boost Studio's executable could not be found for the startup entry.", executable);

        var command = $"\"{executable}\"" + (startMinimized ? " --minimized" : string.Empty);
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }
}
