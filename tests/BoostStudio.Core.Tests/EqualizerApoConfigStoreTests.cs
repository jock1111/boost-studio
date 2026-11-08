using BoostStudio.Core.Dsp;
using BoostStudio.Core.Engine;
using BoostStudio.Core.Models;
using System.Security.AccessControl;
using System.Security.Principal;

namespace BoostStudio.Core.Tests;

public sealed class EqualizerApoConfigStoreTests
{
    private static (EqualizerApoConfigStore Store, TempDirectory Root, string ConfigPath, string Original) Create(
        string? original = null)
    {
        var root = new TempDirectory();
        var configDirectory = root.Subdirectory("config");
        var configPath = Path.Combine(configDirectory, "config.txt");
        if (original is not null)
            File.WriteAllText(configPath, original);

        var installation = new EqualizerApoInstallation(
            root.Path,
            configDirectory,
            configPath,
            Path.Combine(configDirectory, EqualizerApoConfigStore.ManagedFileName),
            "1.4.2",
            "test");

        var store = new EqualizerApoConfigStore(installation, Path.Combine(root.Path, "backups"));
        return (store, root, configPath, original ?? string.Empty);
    }

    [Fact]
    public void EnsureIntegratedPreservesUserConfigurationAndCreatesBackups()
    {
        const string original = "Preamp: -2.5 dB\r\nInclude: MyFilters.txt\r\n";
        var (store, root, configPath, _) = Create(original);

        using (root)
        {
            var changed = store.EnsureIntegrated();

            Assert.True(changed);
            Assert.True(store.IsIntegrationActive);
            var config = File.ReadAllText(configPath);
            Assert.Contains(original.Trim(), config, StringComparison.Ordinal);
            Assert.Contains(EqualizerApoConfigStore.BeginMarker, config, StringComparison.Ordinal);
            Assert.Contains(EqualizerApoConfigStore.EndMarker, config, StringComparison.Ordinal);
            Assert.Contains("Device: all", config, StringComparison.Ordinal);
            Assert.Contains("Stage: post-mix", config, StringComparison.Ordinal);
            Assert.Contains(EqualizerApoConfigStore.IncludeLine, config, StringComparison.Ordinal);
            Assert.Contains("\r\n", config, StringComparison.Ordinal);
            Assert.DoesNotContain("\n", config.Replace("\r\n", "", StringComparison.Ordinal), StringComparison.Ordinal);

            Assert.True(File.Exists(store.BaselinePath));
            Assert.Equal(original, File.ReadAllText(store.BaselinePath));
            Assert.True(File.Exists(Path.Combine(store.ConfigDirectory, EqualizerApoConfigStore.ManagedFileName)));
            Assert.Single(Directory.GetFiles(store.BackupDirectory, "eapo-config-*.txt"));

            Assert.False(store.EnsureIntegrated());
            Assert.Single(Directory.GetFiles(store.BackupDirectory, "eapo-config-*.txt"));
        }
    }

    [Fact]
    public void EnsureIntegratedCreatesCleanConfigWhenNoneExists()
    {
        var (store, root, configPath, _) = Create();

        using (root)
        {
            var changed = store.EnsureIntegrated();

            Assert.True(changed);
            Assert.True(File.Exists(configPath));
            Assert.False(store.HasBaseline);
            Assert.True(store.IsIntegrationActive);
            var config = File.ReadAllText(configPath);
            Assert.StartsWith(EqualizerApoConfigStore.BeginMarker, config, StringComparison.Ordinal);
            Assert.False(Directory.Exists(store.BackupDirectory) &&
                         Directory.GetFiles(store.BackupDirectory, "eapo-config-*.txt").Length > 0);
        }
    }

    [Fact]
    public void EnsureIntegratedRepairsAudioEngineReadAccessEvenWhenContentIsUnchanged()
    {
        var (store, root, configPath, _) = Create("Preamp: 0 dB\n");
        using (root)
        {
            Assert.True(store.EnsureIntegrated());

            // Simulate files written before the ACL fix (or moved from a temp folder):
            // only the interactive user can read them, so audiodg cannot load the DSP.
            MakeOwnerOnly(configPath);
            MakeOwnerOnly(store.ManagedPath);
            Assert.False(EqualizerApoFileAcl.HasAudioEngineReadAccess(configPath));
            Assert.False(EqualizerApoFileAcl.HasAudioEngineReadAccess(store.ManagedPath));

            var changed = store.EnsureIntegrated();

            Assert.False(changed);
            Assert.True(EqualizerApoFileAcl.HasAudioEngineReadAccess(configPath));
            Assert.True(EqualizerApoFileAcl.HasAudioEngineReadAccess(store.ManagedPath));
            Assert.False(store.EnsureIntegrated());
        }
    }

    private static void MakeOwnerOnly(string path)
    {
        var user = WindowsIdentity.GetCurrent().User!;
        var security = new FileSecurity();
        security.AddAccessRule(new FileSystemAccessRule(
            user,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    [Fact]
    public void ManagedConfigurationIsWrittenOnlyWhenContentChanges()
    {
        var (store, root, _, _) = Create("Preamp: 0 dB\n");
        using (root)
        {
            store.EnsureIntegrated();
            var settings = TestSettings.BassOnly();
            settings.AutoHeadroom = true;
            var config = new DspConfigBuilder().Build(settings).Text;

            Assert.True(store.WriteManagedConfig(config));
            Assert.Equal(config, store.ReadManagedText());
            Assert.False(store.WriteManagedConfig(config));
        }
    }

    [Fact]
    public void ManagedConfigurationRejectsInvalidGrammar()
    {
        var (store, root, _, _) = Create();
        using (root)
        {
            store.EnsureIntegrated();
            Assert.Throws<InvalidOperationException>(() =>
                store.WriteManagedConfig("GraphicEQ: 20 0 20000 0\n"));
        }
    }

    [Fact]
    public void RemoveIntegrationRestoresUserContentAndDeletesManagedFile()
    {
        const string original = "# personal filters\nPreamp: -1.0 dB\nInclude: Rock.txt\n";
        var (store, root, configPath, _) = Create(original);
        using (root)
        {
            store.EnsureIntegrated();
            store.WriteManagedConfig(new DspConfigBuilder().Build(TestSettings.BassOnly()).Text);

            var changed = store.RemoveIntegration();

            Assert.True(changed);
            Assert.False(store.IsIntegrationActive);
            Assert.False(File.Exists(store.ManagedPath));
            Assert.Equal(original.Trim(), File.ReadAllText(configPath).Trim());
            Assert.True(Directory.GetFiles(store.BackupDirectory, "eapo-config-*.txt").Length >= 2);
        }
    }

    [Fact]
    public void RestoreBaselineRestoresOriginalAndBacksUpCurrent()
    {
        const string original = "Preamp: -4.0 dB\nInclude: Original.txt\n";
        var (store, root, configPath, _) = Create(original);
        using (root)
        {
            store.EnsureIntegrated();
            store.WriteManagedConfig(new DspConfigBuilder().Build(TestSettings.BassOnly()).Text);
            var backupsBefore = Directory.GetFiles(store.BackupDirectory, "eapo-config-*.txt").Length;

            var restored = store.RestoreBaseline();

            Assert.True(restored);
            Assert.Equal(original, File.ReadAllText(configPath));
            Assert.False(File.Exists(store.ManagedPath));
            Assert.True(Directory.GetFiles(store.BackupDirectory, "eapo-config-*.txt").Length > backupsBefore);
        }
    }

    [Fact]
    public void RestoreBaselineWithoutBackupThrows()
    {
        var (store, root, _, _) = Create();
        using (root)
        {
            Assert.False(store.HasBaseline);
            Assert.Throws<FileNotFoundException>(() => store.RestoreBaseline());
        }
    }

    [Fact]
    public void RemoveManagedBlocksHandlesMultipleBlocksAndPreservesNewlines()
    {
        var text = "A\r\n" +
                   EqualizerApoConfigStore.BeginMarker + "\r\nDevice: all\r\n" +
                   EqualizerApoConfigStore.EndMarker + "\r\nB\r\n" +
                   EqualizerApoConfigStore.BeginMarker + "\r\nInclude: BoostStudio.txt\r\n" +
                   EqualizerApoConfigStore.EndMarker + "\r\nC";

        var stripped = EqualizerApoConfigStore.RemoveManagedBlocks(text);

        Assert.Equal("A\r\nB\r\nC", stripped);
        Assert.True(EqualizerApoConfigStore.ContainsManagedBlock(text));
        Assert.False(EqualizerApoConfigStore.ContainsManagedBlock("Include: other.txt"));
    }

    [Fact]
    public void OrphanBeginMarkerNeverSwallowsFollowingUserConfiguration()
    {
        var text = "Preamp: -2 dB\n" +
                   EqualizerApoConfigStore.BeginMarker + "\n" +
                   "Include: UserRock.txt\n" +
                   "GraphicEQ: 100 3; 1000 -2\n";

        var stripped = EqualizerApoConfigStore.RemoveManagedBlocks(text);

        Assert.Contains("Include: UserRock.txt", stripped, StringComparison.Ordinal);
        Assert.Contains("GraphicEQ: 100 3; 1000 -2", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain(EqualizerApoConfigStore.BeginMarker, stripped, StringComparison.Ordinal);
        Assert.False(EqualizerApoConfigStore.HasCompleteManagedBlock(text));
    }

    [Fact]
    public void OrphanEndMarkerIsRemovedWithoutDeletingUserLines()
    {
        var text = "Preamp: -1 dB\n" +
                   EqualizerApoConfigStore.EndMarker + "\n" +
                   "Include: User.txt\n";

        var stripped = EqualizerApoConfigStore.RemoveManagedBlocks(text);

        Assert.Equal("Preamp: -1 dB\nInclude: User.txt\n", stripped);
    }

    [Fact]
    public void EnsureIntegratedRepairsMalformedBlockAndKeepsSurroundingText()
    {
        const string original = "Preamp: -3 dB\n" +
                                EqualizerApoConfigStore.BeginMarker + "\n" +
                                "Include: User.txt\n";
        var (store, root, configPath, _) = Create(original);

        using (root)
        {
            store.EnsureIntegrated();

            var config = File.ReadAllText(configPath);
            Assert.Contains("Preamp: -3 dB", config, StringComparison.Ordinal);
            Assert.Contains("Include: User.txt", config, StringComparison.Ordinal);
            Assert.True(EqualizerApoConfigStore.HasCompleteManagedBlock(config));
            Assert.Single(
                config.Split(EqualizerApoConfigStore.BeginMarker, StringSplitOptions.None).Skip(1));
        }
    }

    [Fact]
    public void BackupFailureAbortsBeforeChangingAnything()
    {
        const string original = "Preamp: -2 dB\nInclude: User.txt\n";
        var (store, root, configPath, _) = Create(original);

        using (root)
        {
            var blocker = Path.Combine(root.Path, "backup-blocker");
            File.WriteAllText(blocker, "not a directory");
            var failing = new EqualizerApoConfigStore(store.Installation, blocker);

            Assert.Throws<IOException>(() => failing.EnsureIntegrated());

            Assert.Equal(original, File.ReadAllText(configPath));
            Assert.False(File.Exists(store.ManagedPath));
        }
    }

    [Fact]
    public void BaselineDiffersAfterUserEditsAndReintegrationPreservesThem()
    {
        const string original = "Preamp: -2 dB\n";
        var (store, root, configPath, _) = Create(original);

        using (root)
        {
            store.EnsureIntegrated();
            Assert.False(store.BaselineDiffersFromCurrent);

            var withUserEdit = File.ReadAllText(configPath) + "\nInclude: UserAdded.txt\n";
            File.WriteAllText(configPath, withUserEdit);

            Assert.True(store.BaselineDiffersFromCurrent);

            store.EnsureIntegrated();

            var repaired = File.ReadAllText(configPath);
            Assert.Contains("Include: UserAdded.txt", repaired, StringComparison.Ordinal);
            Assert.Equal(original, File.ReadAllText(store.BaselinePath));
        }
    }

    [Fact]
    public void CorruptedManagedFileIsReplacedOnNextWrite()
    {
        var (store, root, _, _) = Create("Preamp: 0 dB\n");

        using (root)
        {
            store.EnsureIntegrated();
            File.WriteAllText(store.ManagedPath, "GraphicEQ: 20 0 20000 0\n# corrupted\n");

            var settings = TestSettings.BassOnly();
            var config = new DspConfigBuilder().Build(settings).Text;
            Assert.True(store.WriteManagedConfig(config));

            Assert.Equal(config, store.ReadManagedText());
            Assert.DoesNotContain("# corrupted", store.ReadManagedText(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LocatorFindsInstallAndConfigDirectories()
    {
        using var root = new TempDirectory();
        var configDirectory = root.Subdirectory("config");
        File.WriteAllText(Path.Combine(configDirectory, "config.txt"), "Preamp: 0 dB\n");
        var locator = new EqualizerApoLocator();

        var fromInstall = locator.CreateFromPath(root.Path, out var installError);
        var fromConfig = locator.CreateFromPath(configDirectory, out var configError);
        var missing = locator.CreateFromPath(root.Subdirectory("empty"), out var missingError);

        Assert.Null(installError);
        Assert.NotNull(fromInstall);
        Assert.Equal(configDirectory, fromInstall!.ConfigDirectory);
        Assert.Equal(Path.Combine(configDirectory, "BoostStudio.txt"), fromInstall.ManagedConfigPath);

        Assert.Null(configError);
        Assert.NotNull(fromConfig);
        Assert.Equal(configDirectory, fromConfig!.ConfigDirectory);

        Assert.Null(missing);
        Assert.NotNull(missingError);
        Assert.Contains("config.txt", missingError!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CanWriteToDirectoryCreatesAndCleansProbe()
    {
        using var root = new TempDirectory();
        var path = Path.Combine(root.Path, "writable");

        Assert.True(EqualizerApoLocator.CanWriteToDirectory(path));
        Assert.Empty(Directory.GetFiles(path));
    }
}
