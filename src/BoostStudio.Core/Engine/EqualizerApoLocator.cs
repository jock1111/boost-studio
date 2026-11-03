using System.Diagnostics;
using Microsoft.Win32;
using BoostStudio.Core.Models;
using BoostStudio.Core.Storage;

namespace BoostStudio.Core.Engine;

/// <summary>
/// Finds an Equalizer APO installation without requiring its editor to be running.
/// Detection uses Equalizer APO's documented ConfigPath registry value first, then the
/// uninstall entry and standard installation directories.
/// </summary>
public sealed class EqualizerApoLocator
{
    private readonly ILogService? _log;

    public EqualizerApoLocator(ILogService? log = null)
    {
        _log = log;
    }

    public EqualizerApoInstallation? Locate(string? overridePath, out string? error)
    {
        error = null;

        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var custom = ResolveConfigDirectory(overridePath);
            if (custom is null)
            {
                error = "The custom Equalizer APO path does not contain a config.txt file.";
                return null;
            }

            return Create(custom, "Custom path");
        }

        foreach (var candidate in GetRegistryConfigPaths())
        {
            var resolved = ResolveConfigDirectory(candidate);
            if (resolved is not null)
                return Create(resolved, "Registry ConfigPath");
        }

        var uninstall = ReadUninstallEntry();
        if (!string.IsNullOrWhiteSpace(uninstall.InstallLocation))
        {
            var resolved = ResolveConfigDirectory(uninstall.InstallLocation);
            if (resolved is not null)
                return Create(resolved, "Uninstall entry", uninstall.DisplayVersion);
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        foreach (var root in new[] { programFiles, programFilesX86 })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var installDirectory = Path.Combine(root, "EqualizerAPO");
            var resolved = ResolveConfigDirectory(installDirectory);
            if (resolved is not null)
                return Create(resolved, "Standard install directory", uninstall.DisplayVersion);
        }

        return null;
    }

    /// <summary>Used by tests and by manual path selection.</summary>
    public EqualizerApoInstallation? CreateFromPath(string configOrInstallPath, out string? error)
    {
        error = null;
        var configDirectory = ResolveConfigDirectory(configOrInstallPath);
        if (configDirectory is null)
        {
            error = "No Equalizer APO config.txt was found at that location.";
            return null;
        }

        return Create(configDirectory, "Custom path");
    }

    private EqualizerApoInstallation Create(string configDirectory, string source, string? version = null)
    {
        configDirectory = Path.GetFullPath(configDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var installDirectory = Directory.GetParent(configDirectory)?.FullName ?? configDirectory;
        var configTxt = Path.Combine(configDirectory, "config.txt");
        var detectedVersion = version ?? ReadVersion(installDirectory) ?? ReadVersion(configDirectory);

        return new EqualizerApoInstallation(
            installDirectory,
            configDirectory,
            configTxt,
            Path.Combine(configDirectory, "BoostStudio.txt"),
            detectedVersion,
            source);
    }

    private static string? ResolveConfigDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            if (File.Exists(path))
            {
                if (string.Equals(Path.GetFileName(path), "config.txt", StringComparison.OrdinalIgnoreCase))
                    return Path.GetDirectoryName(path);
                return null;
            }

            if (!Directory.Exists(path))
                return null;

            // Path itself is a config directory.
            if (File.Exists(Path.Combine(path, "config.txt")))
                return path;

            // Path is an installation directory.
            var configSubdirectory = Path.Combine(path, "config");
            if (File.Exists(Path.Combine(configSubdirectory, "config.txt")))
                return configSubdirectory;

            // Some installers use a different folder with EqualizerAPO.dll present.
            if (Directory.Exists(configSubdirectory) &&
                (File.Exists(Path.Combine(path, "EqualizerAPO.dll")) ||
                 File.Exists(Path.Combine(path, "EqualizerAPO64.dll"))))
                return configSubdirectory;
        }
        catch (Exception ex)
        {
            // Invalid characters, permissions and other path errors.
            Debug.WriteLine(ex.Message);
        }

        return null;
    }

    private IEnumerable<string> GetRegistryConfigPaths()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            string? value = null;
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = baseKey.OpenSubKey(@"SOFTWARE\EqualizerAPO");
                value = key?.GetValue("ConfigPath") as string;
            }
            catch (Exception ex)
            {
                _log?.Warn($"Could not read Equalizer APO registry ({view}): {ex.Message}");
            }

            if (!string.IsNullOrWhiteSpace(value))
                yield return value;
        }
    }

    private (string? InstallLocation, string? DisplayVersion) ReadUninstallEntry()
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (uninstall is null) continue;

                    foreach (var subKeyName in uninstall.GetSubKeyNames())
                    {
                        using var subKey = uninstall.OpenSubKey(subKeyName);
                        var displayName = subKey?.GetValue("DisplayName") as string;
                        if (displayName is null ||
                            !displayName.Contains("Equalizer APO", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var installLocation = subKey?.GetValue("InstallLocation") as string;
                        var displayIcon = subKey?.GetValue("DisplayIcon") as string;
                        var version = subKey?.GetValue("DisplayVersion") as string;

                        if (string.IsNullOrWhiteSpace(installLocation) && !string.IsNullOrWhiteSpace(displayIcon))
                        {
                            var iconPath = displayIcon.Trim('"').Split(',')[0];
                            installLocation = Directory.Exists(iconPath)
                                ? iconPath
                                : Path.GetDirectoryName(iconPath);
                        }

                        return (installLocation, version);
                    }
                }
                catch (Exception ex)
                {
                    _log?.Warn($"Could not inspect {hive}/{view} uninstall entries: {ex.Message}");
                }
            }
        }

        return (null, null);
    }

    private static string? ReadVersion(string directory)
    {
        try
        {
            var candidates = new[]
            {
                Path.Combine(directory, "EqualizerAPO.dll"),
                Path.Combine(directory, "EqualizerAPO64.dll"),
                Path.Combine(directory, "Editor.exe")
            };

            foreach (var candidate in candidates)
            {
                if (!File.Exists(candidate)) continue;
                var version = FileVersionInfo.GetVersionInfo(candidate).FileVersion;
                if (!string.IsNullOrWhiteSpace(version))
                    return version;
            }
        }
        catch
        {
            // Version is informative only.
        }

        return null;
    }

    public static bool CanWriteToDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".booststudio-write-{Guid.NewGuid():N}.tmp");
            using (File.Create(probe)) { }
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
