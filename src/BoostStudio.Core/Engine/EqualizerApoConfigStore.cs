using System.Text;
using BoostStudio.Core.Dsp;
using BoostStudio.Core.Models;
using BoostStudio.Core.Storage;

namespace BoostStudio.Core.Engine;

/// <summary>
/// Manages only a clearly marked include block in Equalizer APO's config.txt and a
/// separate BoostStudio.txt file. Existing user configuration is preserved, backed up
/// before every config.txt change and restored on demand.
///
/// Reliability rules:
///  * Only a complete, ordered marker pair is treated as a managed block.
///  * A malformed or orphan marker is removed without deleting the text around it.
///  * config.txt is re-read immediately before every write so a concurrent user edit
///    is retried instead of being overwritten.
///  * A backup failure aborts the change before anything is modified.
/// </summary>
public sealed class EqualizerApoConfigStore
{
    public const string BeginMarker = "# >>> BOOST STUDIO MANAGED INCLUDE - DO NOT EDIT >>>";
    public const string EndMarker = "# <<< BOOST STUDIO MANAGED INCLUDE <<<";
    public const string IncludeLine = "Include: BoostStudio.txt";
    public const string ManagedFileName = "BoostStudio.txt";
    public const string ManagedPlaceholder =
        "# Boost Studio DSP configuration placeholder.\n" +
        "# Enable a module in Boost Studio to write filters here.\n";

    private const int MaxWriteAttempts = 4;

    private readonly ILogService? _log;

    public EqualizerApoConfigStore(EqualizerApoInstallation installation, string? backupDirectory = null, ILogService? log = null)
    {
        Installation = installation;
        ConfigDirectory = installation.ConfigDirectory;
        ConfigPath = installation.ConfigTxtPath;
        ManagedPath = installation.ManagedConfigPath;
        BackupDirectory = backupDirectory ?? AppPaths.BackupsDirectory;
        _log = log;
    }

    public EqualizerApoInstallation Installation { get; }

    public string ConfigDirectory { get; }

    public string ConfigPath { get; }

    public string ManagedPath { get; }

    public string BackupDirectory { get; }

    public string BaselinePath => Path.Combine(BackupDirectory, "eapo-baseline-config.txt");

    public bool HasBaseline => File.Exists(BaselinePath);

    /// <summary>True when a complete managed block and its include target are present.</summary>
    public bool IsIntegrationActive
    {
        get
        {
            try
            {
                if (!File.Exists(ConfigPath) || !File.Exists(ManagedPath))
                    return false;

                var text = File.ReadAllText(ConfigPath, Encoding.UTF8);
                return HasCompleteManagedBlock(text);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Markers or the include line exist, even if the block is malformed.</summary>
    public bool HasManagedTrace
    {
        get
        {
            try
            {
                return File.Exists(ConfigPath) && ContainsManagedBlock(ReadConfigText());
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// True when the current user configuration (without Boost Studio's block) differs
    /// from the baseline captured before the first integration.
    /// </summary>
    public bool BaselineDiffersFromCurrent
    {
        get
        {
            try
            {
                if (!File.Exists(BaselinePath) || !File.Exists(ConfigPath))
                    return false;

                var current = Normalize(RemoveManagedBlocks(ReadConfigText())).Trim();
                var baseline = Normalize(File.ReadAllText(BaselinePath, Encoding.UTF8)).Trim();
                return !string.Equals(current, baseline, StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }
    }

    public string ReadConfigText() =>
        File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath, Encoding.UTF8) : string.Empty;

    public string ReadManagedText() =>
        File.Exists(ManagedPath) ? File.ReadAllText(ManagedPath, Encoding.UTF8) : string.Empty;

    public bool EnsureIntegrated()
    {
        Directory.CreateDirectory(ConfigDirectory);

        for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
        {
            var existing = ReadConfigText();
            var newline = existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var hadCompleteBlock = HasCompleteManagedBlock(existing);
            var hadManagedFile = File.Exists(ManagedPath);
            var stripped = RemoveManagedBlocks(existing);
            var result = BuildConfigWithBlock(stripped, newline);

            if (hadCompleteBlock && hadManagedFile && TextEquals(existing, result))
                return false;

            var configExists = File.Exists(ConfigPath);
            if ((configExists || existing.Length > 0) &&
                (!hadCompleteBlock || !TextEquals(existing, result)))
            {
                CaptureBaseline(existing);
                CreateTimestampedBackup(existing);
            }

            // Create the include target before config.txt starts referencing it.
            if (!hadManagedFile)
                AtomicFile.WriteAllText(ManagedPath, BuildManagedPlaceholder(), createBackup: false,
                    temporaryDirectory: AppPaths.RuntimeDirectory);

            if (TextEquals(existing, result))
                return !hadManagedFile;

            if (!TryWriteIfUnchanged(existing, result))
                continue;

            _log?.Info("Installed or repaired Boost Studio's managed include in Equalizer APO config.txt.");
            return true;
        }

        throw new IOException(
            "Equalizer APO's config.txt changed repeatedly while Boost Studio was updating it. Nothing was overwritten; try again.");
    }

    /// <summary>Writes the generated DSP configuration to BoostStudio.txt. Returns true if changed.</summary>
    public bool WriteManagedConfig(string configText)
    {
        var errors = DspConfigBuilder.Validate(configText);
        if (errors.Count > 0)
            throw new InvalidOperationException("Generated DSP configuration is invalid: " + string.Join("; ", errors));

        var existing = ReadManagedText();
        if (TextEquals(existing, configText))
            return false;

        Directory.CreateDirectory(ConfigDirectory);
        AtomicFile.WriteAllText(ManagedPath, configText, createBackup: false,
            temporaryDirectory: AppPaths.RuntimeDirectory);
        _log?.Info($"Applied DSP configuration ({configText.Length} characters).");
        return true;
    }

    public bool RemoveIntegration()
    {
        var changed = false;

        for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
        {
            if (!File.Exists(ConfigPath))
                break;

            var existing = ReadConfigText();
            if (!ContainsManagedBlock(existing))
                break;

            var newline = existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var stripped = RemoveManagedBlocks(existing).TrimEnd('\r', '\n');
            if (stripped.Length > 0)
                stripped += newline;

            CreateTimestampedBackup(existing);
            if (!TryWriteIfUnchanged(existing, stripped))
                continue;

            changed = true;
            _log?.Info("Removed Boost Studio's managed include. Unrelated Equalizer APO configuration was preserved.");
            break;
        }

        if (File.Exists(ManagedPath))
        {
            try
            {
                File.Delete(ManagedPath);
                changed = true;
            }
            catch (Exception ex)
            {
                _log?.Warn($"Could not delete the managed DSP file: {ex.Message}");
            }
        }

        return changed;
    }

    /// <summary>
    /// Replaces config.txt with the exact pre-Boost baseline. This is intentionally
    /// destructive: the current file is backed up first and the caller must confirm.
    /// For ordinary uninstall/disconnect, use <see cref="RemoveIntegration"/> instead.
    /// </summary>
    public bool RestoreBaseline()
    {
        if (!File.Exists(BaselinePath))
            throw new FileNotFoundException("No pre-Boost Equalizer APO baseline backup was found.", BaselinePath);

        var current = ReadConfigText();
        if (File.Exists(ConfigPath))
            CreateTimestampedBackup(current);

        var baseline = File.ReadAllText(BaselinePath, Encoding.UTF8);
        AtomicFile.WriteAllText(ConfigPath, baseline, createBackup: false,
            temporaryDirectory: AppPaths.RuntimeDirectory);

        if (File.Exists(ManagedPath))
        {
            try { File.Delete(ManagedPath); }
            catch { /* best effort */ }
        }

        _log?.Info("Restored the pre-Boost Equalizer APO configuration.");
        return true;
    }

    /// <summary>Re-reads config.txt immediately before writing; retries if it changed.</summary>
    private bool TryWriteIfUnchanged(string expected, string content)
    {
        for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
        {
            if (!TextEquals(ReadConfigText(), expected))
                return false;

            try
            {
                AtomicFile.WriteAllText(ConfigPath, content, createBackup: false,
                    temporaryDirectory: AppPaths.RuntimeDirectory);
                return true;
            }
            catch (IOException) when (attempt < MaxWriteAttempts - 1)
            {
                Thread.Sleep(80);
            }
        }

        throw new IOException("Could not write Equalizer APO's config.txt. Another process may be holding the file.");
    }

    private void CaptureBaseline(string existingConfig)
    {
        try
        {
            Directory.CreateDirectory(BackupDirectory);
            if (File.Exists(BaselinePath))
                return;

            File.WriteAllText(BaselinePath, existingConfig, new UTF8Encoding(false));
            _log?.Info($"Saved Equalizer APO baseline to {BaselinePath}.");
        }
        catch (Exception ex)
        {
            throw new IOException("Could not preserve Equalizer APO's original configuration. Nothing was changed.", ex);
        }
    }

    private void CreateTimestampedBackup(string configText)
    {
        try
        {
            Directory.CreateDirectory(BackupDirectory);
            var path = Path.Combine(BackupDirectory,
                $"eapo-config-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.txt");
            File.WriteAllText(path, configText, new UTF8Encoding(false));

            var backups = new DirectoryInfo(BackupDirectory)
                .GetFiles("eapo-config-*.txt")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(40)
                .ToArray();
            foreach (var old in backups)
            {
                try { old.Delete(); } catch { /* ignore */ }
            }

            _log?.Info($"Backed up Equalizer APO config to {path}.");
        }
        catch (Exception ex)
        {
            // A backup failure must abort integration; the caller would otherwise
            // modify a config it could not preserve.
            throw new IOException("Could not create an Equalizer APO configuration backup. Nothing was changed.", ex);
        }
    }

    private static string BuildConfigWithBlock(string strippedContent, string newline)
    {
        var builder = new StringBuilder();
        var trimmed = strippedContent.TrimEnd('\r', '\n');
        if (trimmed.Length > 0)
        {
            builder.Append(Normalize(trimmed).Replace("\n", newline));
            builder.Append(newline).Append(newline);
        }

        builder.Append(BeginMarker).Append(newline);
        builder.Append("Device: all").Append(newline);
        builder.Append("Stage: post-mix").Append(newline);
        builder.Append(IncludeLine).Append(newline);
        builder.Append(EndMarker).Append(newline);
        return builder.ToString();
    }

    /// <summary>True when any marker or the managed include line is present.</summary>
    public static bool ContainsManagedBlock(string text) =>
        text.Contains(BeginMarker, StringComparison.Ordinal) ||
        text.Contains(EndMarker, StringComparison.Ordinal) ||
        text.Split('\n').Any(line => IsIncludeLine(line.Trim()));

    /// <summary>
    /// A complete block has a begin marker, a later end marker and the exact managed
    /// include line between them. Everything else is treated as malformed.
    /// </summary>
    public static bool HasCompleteManagedBlock(string text) =>
        HasCompleteManagedBlock(text, out _, out _);

    public static bool HasCompleteManagedBlock(string text, out int beginLine, out int endLine)
    {
        beginLine = -1;
        endLine = -1;
        if (string.IsNullOrEmpty(text))
            return false;

        var lines = Normalize(text).Split('\n');
        var begin = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith(BeginMarker, StringComparison.Ordinal))
            {
                begin = i;
                continue;
            }

            if (begin < 0)
                continue;

            if (trimmed.StartsWith(EndMarker, StringComparison.Ordinal))
            {
                var hasInclude = false;
                for (var j = begin + 1; j < i; j++)
                {
                    if (IsIncludeLine(lines[j].Trim()))
                    {
                        hasInclude = true;
                        break;
                    }
                }

                if (hasInclude)
                {
                    beginLine = begin;
                    endLine = i;
                    return true;
                }

                begin = -1;
            }
        }

        return false;
    }

    public static string RemoveManagedBlocks(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = Normalize(text).Split('\n');
        var result = new List<string>(lines.Length);

        var index = 0;
        while (index < lines.Length)
        {
            var trimmed = lines[index].Trim();

            if (trimmed.StartsWith(BeginMarker, StringComparison.Ordinal))
            {
                // Only remove text between a complete marker pair. A lone begin marker
                // must never swallow the rest of the user's configuration.
                var end = -1;
                for (var candidate = index + 1; candidate < lines.Length; candidate++)
                {
                    if (lines[candidate].Trim().StartsWith(EndMarker, StringComparison.Ordinal))
                    {
                        end = candidate;
                        break;
                    }
                }

                if (end >= 0)
                {
                    index = end + 1;
                    continue;
                }

                index++;
                continue;
            }

            if (trimmed.StartsWith(EndMarker, StringComparison.Ordinal))
            {
                index++;
                continue;
            }

            if (IsIncludeLine(trimmed))
            {
                index++;
                continue;
            }

            result.Add(lines[index]);
            index++;
        }

        return string.Join(newline, result);
    }

    private static bool IsIncludeLine(string trimmed) =>
        string.Equals(trimmed, IncludeLine, StringComparison.OrdinalIgnoreCase);

    private static string BuildManagedPlaceholder() => ManagedPlaceholder;

    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n');

    private static bool TextEquals(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);
}
