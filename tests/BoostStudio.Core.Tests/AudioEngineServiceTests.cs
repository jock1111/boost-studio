using BoostStudio.Core.Engine;
using BoostStudio.Core.Models;

namespace BoostStudio.Core.Tests;

public sealed class AudioEngineServiceTests
{
    [Fact]
    public async Task ReportsNotInstalledWhenNoEqualizerApoFound()
    {
        using var root = new TempDirectory();
        using var service = new AudioEngineService(backupDirectory: root.Subdirectory("backups"), apoProbe: new FixedApoProbe());
        var settings = TestSettings.BassOnly();
        settings.EqualizerApoPathOverride = root.Subdirectory("nonexistent");

        var result = await service.ApplyAsync(settings);

        Assert.False(result.Success);
        Assert.Equal(EngineState.NotInstalled, result.Status.State);
        Assert.False(result.Status.Installed);
        Assert.False(result.Status.IntegrationActive);
    }

    [Fact]
    public async Task IdleWhenInstalledButNoModulesEnabled()
    {
        using var root = new TempDirectory();
        var configDir = root.Subdirectory("config");
        File.WriteAllText(Path.Combine(configDir, "config.txt"), "Preamp: 0 dB\n");

        using var service = new AudioEngineService(backupDirectory: root.Subdirectory("backups"), apoProbe: new FixedApoProbe());
        var settings = AppSettings.CreateDefault();
        settings.EqualizerApoPathOverride = configDir;
        settings.Bass.Enabled = false;
        settings.Volume.Enabled = false;
        settings.Equalizer.Enabled = false;

        var result = await service.ApplyAsync(settings);

        Assert.True(result.Success);
        Assert.Equal(EngineState.NotIntegrated, result.Status.State);
        Assert.True(result.Status.Installed);
        Assert.False(result.Status.IntegrationActive);
    }

    [Fact]
    public async Task IntegratesAndAppliesWhenBassEnabled()
    {
        using var root = new TempDirectory();
        var configDir = root.Subdirectory("config");
        var configTxt = Path.Combine(configDir, "config.txt");
        File.WriteAllText(configTxt, "Preamp: -2 dB\n");

        using var service = new AudioEngineService(backupDirectory: root.Subdirectory("backups"), apoProbe: new FixedApoProbe());
        var settings = TestSettings.BassOnly();
        settings.EqualizerApoPathOverride = configDir;

        var result = await service.ApplyAsync(settings);

        Assert.True(result.Success);
        Assert.Equal(EngineState.Ready, result.Status.State);
        Assert.True(result.Status.Installed);
        Assert.True(result.Status.IntegrationActive);
        Assert.True(result.Status.ManagedFilePresent);
        Assert.Equal(1, result.Status.ActiveFilterCount);

        var configText = File.ReadAllText(configTxt);
        Assert.Contains(EqualizerApoConfigStore.BeginMarker, configText);
        Assert.Contains(EqualizerApoConfigStore.IncludeLine, configText);

        var managedPath = Path.Combine(configDir, EqualizerApoConfigStore.ManagedFileName);
        Assert.True(File.Exists(managedPath));
        var managedText = File.ReadAllText(managedPath);
        Assert.Contains("Filter 1: ON LS", managedText);
    }

    [Fact]
    public async Task MasterBypassTransitionsToBypassedState()
    {
        using var root = new TempDirectory();
        var configDir = root.Subdirectory("config");
        var configTxt = Path.Combine(configDir, "config.txt");
        File.WriteAllText(configTxt, "Preamp: 0 dB\n");

        using var service = new AudioEngineService(backupDirectory: root.Subdirectory("backups"), apoProbe: new FixedApoProbe());
        var settings = TestSettings.BassOnly();
        settings.EqualizerApoPathOverride = configDir;

        await service.ApplyAsync(settings);

        settings.ProcessingEnabled = false;
        var result = await service.ApplyAsync(settings);

        Assert.True(result.Success);
        Assert.Equal(EngineState.Bypassed, result.Status.State);
        Assert.True(result.Status.IntegrationActive);
        Assert.Equal(0, result.Status.ActiveFilterCount);

        var managedPath = Path.Combine(configDir, EqualizerApoConfigStore.ManagedFileName);
        var managedText = File.ReadAllText(managedPath);
        Assert.Contains("MASTER BYPASS", managedText);
    }

    [Fact]
    public async Task RemoveIntegrationCleansUpConfigAndManagedFile()
    {
        using var root = new TempDirectory();
        var configDir = root.Subdirectory("config");
        var configTxt = Path.Combine(configDir, "config.txt");
        File.WriteAllText(configTxt, "Preamp: -3 dB\n");

        using var service = new AudioEngineService(backupDirectory: root.Subdirectory("backups"), apoProbe: new FixedApoProbe());
        var settings = TestSettings.BassOnly();
        settings.EqualizerApoPathOverride = configDir;

        await service.ApplyAsync(settings);
        Assert.True(service.Status.IntegrationActive);

        var status = await service.RemoveIntegrationAsync(settings);

        Assert.Equal(EngineState.NotIntegrated, status.State);
        Assert.False(status.IntegrationActive);
        Assert.False(status.ManagedFilePresent);

        var configText = File.ReadAllText(configTxt);
        Assert.DoesNotContain(EqualizerApoConfigStore.BeginMarker, configText);
        Assert.DoesNotContain(EqualizerApoConfigStore.IncludeLine, configText);
        Assert.Contains("Preamp: -3 dB", configText);

        var managedPath = Path.Combine(configDir, EqualizerApoConfigStore.ManagedFileName);
        Assert.False(File.Exists(managedPath));
    }

    [Fact]
    public async Task RestoreBaselineRestoresPreBoostConfig()
    {
        using var root = new TempDirectory();
        var configDir = root.Subdirectory("config");
        var configTxt = Path.Combine(configDir, "config.txt");
        const string originalContent = "Preamp: -1 dB\nInclude: UserFilters.txt\n";
        File.WriteAllText(configTxt, originalContent);

        using var service = new AudioEngineService(backupDirectory: root.Subdirectory("backups"), apoProbe: new FixedApoProbe());
        var settings = TestSettings.BassOnly();
        settings.EqualizerApoPathOverride = configDir;

        await service.ApplyAsync(settings);

        var status = await service.RestoreBaselineAsync(settings);

        Assert.False(status.IntegrationActive);
        Assert.Equal(originalContent, File.ReadAllText(configTxt));
    }

    [Fact]
    public async Task ReconnectReestablishesIntegration()
    {
        using var root = new TempDirectory();
        var configDir = root.Subdirectory("config");
        var configTxt = Path.Combine(configDir, "config.txt");
        File.WriteAllText(configTxt, "Preamp: 0 dB\n");

        using var service = new AudioEngineService(backupDirectory: root.Subdirectory("backups"), apoProbe: new FixedApoProbe());
        var settings = TestSettings.BassOnly();
        settings.EqualizerApoPathOverride = configDir;

        await service.ApplyAsync(settings);
        await service.RemoveIntegrationAsync(settings);
        Assert.False(service.Status.IntegrationActive);

        var status = await service.ReconnectAsync(settings);

        Assert.Equal(EngineState.Ready, status.State);
        Assert.True(status.IntegrationActive);
        Assert.True(status.ManagedFilePresent);
    }

    [Fact]
    public async Task ReportsApoNotEnabledWhenConfigurationWasWrittenButApoIsDetached()
    {
        using var root = new TempDirectory();
        var configDir = root.Subdirectory("config");
        var configTxt = Path.Combine(configDir, "config.txt");
        File.WriteAllText(configTxt, "Preamp: 0 dB\n");

        using var service = new AudioEngineService(
            backupDirectory: root.Subdirectory("backups"),
            apoProbe: new FixedApoProbe(targetEnabled: false, enabledCount: 0));
        var settings = TestSettings.BassOnly();
        settings.EqualizerApoPathOverride = configDir;

        var result = await service.ApplyAsync(settings);

        Assert.True(result.Success);
        Assert.Equal(EngineState.ApoNotEnabled, result.Status.State);
        Assert.False(result.Status.IsReady);
        Assert.True(result.Status.NeedsApoSetup);
        Assert.True(result.Status.ApoStatusChecked);
        Assert.False(result.Status.TargetApoEnabled);
        Assert.Equal(0, result.Status.ApoEnabledDeviceCount);
        Assert.NotNull(result.Status.VerificationText);
        Assert.Contains("not verified", result.Status.VerificationText, StringComparison.OrdinalIgnoreCase);

        // The configuration must still be written; only the audible claim is withheld.
        Assert.True(result.Status.IntegrationActive);
        Assert.Contains(EqualizerApoConfigStore.BeginMarker, File.ReadAllText(configTxt));
    }

    [Fact]
    public async Task ReportsApoAssociationWithoutClaimingAudibleVerification()
    {
        using var root = new TempDirectory();
        var configDir = root.Subdirectory("config");
        File.WriteAllText(Path.Combine(configDir, "config.txt"), "Preamp: 0 dB\n");

        using var service = new AudioEngineService(
            backupDirectory: root.Subdirectory("backups"),
            apoProbe: new FixedApoProbe(targetEnabled: true, enabledCount: 1));
        var settings = TestSettings.BassOnly();
        settings.EqualizerApoPathOverride = configDir;

        var result = await service.ApplyAsync(settings);

        Assert.Equal(EngineState.Ready, result.Status.State);
        Assert.True(result.Status.TargetApoEnabled);
        Assert.True(result.Status.ApoStatusChecked);
        Assert.Equal(1, result.Status.ApoEnabledDeviceCount);
        Assert.Contains("not measured", result.Status.VerificationText!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fully operational", result.Status.VerificationText!, StringComparison.OrdinalIgnoreCase);
    }
}
