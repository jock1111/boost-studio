using System.IO.Compression;
using System.Text;
using System.Text.Json;
using BoostStudio.Core.Models;

namespace BoostStudio.Core.Storage;

public sealed record BackupRestoreResult(bool Success, string Message, AppSettings? Settings = null, UserPresetLibrary? Presets = null);

/// <summary>
/// Creates a portable .boostbackup zip containing settings and user EQ presets.
/// Import is allow-list based: only known JSON entries are read, never extracted to disk.
/// </summary>
public sealed class BackupService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SettingsStore _settingsStore;
    private readonly PresetStore _presetStore;
    private readonly ILogService? _log;

    public BackupService(SettingsStore settingsStore, PresetStore presetStore, ILogService? log = null)
    {
        _settingsStore = settingsStore;
        _presetStore = presetStore;
        _log = log;
    }

    public void Export(string destinationPath, AppSettings settings, UserPresetLibrary presets, string? diagnostics = null)
    {
        var temporary = destinationPath + ".tmp";
        try
        {
            if (File.Exists(temporary)) File.Delete(temporary);

            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "settings.json", JsonSerializer.Serialize(settings, JsonOptions));
                WriteEntry(archive, "presets.json", JsonSerializer.Serialize(presets, JsonOptions));
                WriteEntry(archive, "manifest.json", JsonSerializer.Serialize(new
                {
                    product = AppInfo.ProductName,
                    version = AppInfo.Version,
                    exportedUtc = DateTime.UtcNow,
                    format = 1
                }, JsonOptions));

                if (!string.IsNullOrWhiteSpace(diagnostics))
                    WriteEntry(archive, "diagnostics.txt", diagnostics);
            }

            File.Move(temporary, destinationPath, overwrite: true);
            _log?.Info($"Exported settings backup to {destinationPath}.");
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch
            {
                // Ignore.
            }
        }
    }

    public BackupRestoreResult Import(string sourcePath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(sourcePath);
            var settingsEntry = archive.GetEntry("settings.json");
            if (settingsEntry is null)
                return new BackupRestoreResult(false, "This file is not a Boost Studio backup (settings.json is missing).");

            AppSettings? settings;
            using (var stream = settingsEntry.Open())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                settings = JsonSerializer.Deserialize<AppSettings>(reader.ReadToEnd(), new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    AllowTrailingCommas = true,
                    ReadCommentHandling = JsonCommentHandling.Skip
                });
            }

            if (settings is null)
                return new BackupRestoreResult(false, "The backup's settings data is invalid.");

            settings.Normalize();

            UserPresetLibrary presets = new();
            var presetEntry = archive.GetEntry("presets.json");
            if (presetEntry is not null)
            {
                using var stream = presetEntry.Open();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                presets = JsonSerializer.Deserialize<UserPresetLibrary>(reader.ReadToEnd(), new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? new UserPresetLibrary();
                presets.Normalize();
            }

            return new BackupRestoreResult(true, "Backup verified.", settings, presets);
        }
        catch (Exception ex)
        {
            _log?.Error($"Could not import backup {sourcePath}.", ex);
            return new BackupRestoreResult(false, $"Could not open the backup: {ex.Message}");
        }
    }

    public void ApplyImported(AppSettings settings, UserPresetLibrary presets)
    {
        _settingsStore.Save(settings);
        _presetStore.Save(presets);
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }
}
