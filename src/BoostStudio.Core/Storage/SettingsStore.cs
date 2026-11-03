using System.Text;
using System.Text.Json;
using BoostStudio.Core.Models;

namespace BoostStudio.Core.Storage;

/// <summary>
/// Loads and writes JSON state atomically. A backup copy is kept so that a corrupt
/// settings file cannot permanently lose the user's configuration.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private readonly string _path;
    private readonly ILogService? _log;

    public SettingsStore(string? path = null, ILogService? log = null)
    {
        _path = path ?? AppPaths.SettingsFile;
        _log = log;
    }

    public string FilePath => _path;

    public AppSettings Load()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (!File.Exists(_path))
                return AppSettings.CreateDefault();

            var json = File.ReadAllText(_path, Encoding.UTF8);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? AppSettings.CreateDefault();
            settings.Normalize();
            return settings;
        }
        catch (Exception ex)
        {
            _log?.Error($"Could not load settings from {_path}. Attempting backup.", ex);
            return TryLoadBackup() ?? AppSettings.CreateDefault();
        }
    }

    private AppSettings? TryLoadBackup()
    {
        var backup = _path + ".bak";
        try
        {
            if (!File.Exists(backup))
                return null;

            var json = File.ReadAllText(backup, Encoding.UTF8);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (settings is null)
                return null;

            settings.Normalize();
            _log?.Info("Recovered settings from the automatic backup.");
            QuarantineCorruptFile();
            return settings;
        }
        catch (Exception ex)
        {
            _log?.Error("Could not recover settings from backup.", ex);
            return null;
        }
    }

    private void QuarantineCorruptFile()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var quarantine = $"{_path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(_path, quarantine);
        }
        catch
        {
            // Ignore.
        }
    }

    public void Save(AppSettings settings)
    {
        settings.Normalize();
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        AtomicFile.WriteAllText(_path, json, createBackup: true);
    }
}

public sealed class PresetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private readonly string _path;
    private readonly ILogService? _log;

    public PresetStore(string? path = null, ILogService? log = null)
    {
        _path = path ?? AppPaths.UserPresetsFile;
        _log = log;
    }

    public string FilePath => _path;

    public UserPresetLibrary Load()
    {
        try
        {
            if (!File.Exists(_path))
                return new UserPresetLibrary();

            var json = File.ReadAllText(_path, Encoding.UTF8);
            var library = JsonSerializer.Deserialize<UserPresetLibrary>(json, JsonOptions) ?? new UserPresetLibrary();
            library.Normalize();
            return library;
        }
        catch (Exception ex)
        {
            _log?.Error("Could not load user presets.", ex);
            return new UserPresetLibrary();
        }
    }

    public void Save(UserPresetLibrary library)
    {
        library.Normalize();
        var json = JsonSerializer.Serialize(library, JsonOptions);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        AtomicFile.WriteAllText(_path, json, createBackup: false);
    }
}

public static class AtomicFile
{
    public static void WriteAllText(string path, string content, bool createBackup, string? temporaryDirectory = null)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        // Writing the temporary file outside a protected Equalizer APO folder lets a
        // standard user update config.txt with only Modify on the destination files.
        var tempDirectory = string.IsNullOrWhiteSpace(temporaryDirectory) ? directory : temporaryDirectory;
        Directory.CreateDirectory(tempDirectory);
        var temporary = Path.Combine(tempDirectory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var sameVolume = string.Equals(Path.GetPathRoot(temporary), Path.GetPathRoot(path),
            StringComparison.OrdinalIgnoreCase);

        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (!sameVolume)
            {
                if (File.Exists(path) && createBackup)
                    File.Copy(path, path + ".bak", overwrite: true);
                File.Move(temporary, path, overwrite: true);
            }
            else if (File.Exists(path) && createBackup)
            {
                var backup = path + ".bak";
                try
                {
                    File.Replace(temporary, path, backup, ignoreMetadataErrors: true);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Copy(path, backup, overwrite: true);
                    File.Move(temporary, path, overwrite: true);
                }
                catch (IOException)
                {
                    File.Copy(path, backup, overwrite: true);
                    File.Move(temporary, path, overwrite: true);
                }
            }
            else
            {
                File.Move(temporary, path, overwrite: true);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            catch
            {
                // Ignore temp cleanup failures.
            }
        }
    }
}
