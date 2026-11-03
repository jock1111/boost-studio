using BoostStudio.Core.Engine;

namespace BoostStudio.Core.Tests;

public sealed class ApoAssociationTests
{
    [Fact]
    public void ExtractsEndpointKeyFromMmDeviceId()
    {
        const string endpoint = "{0a8c479c-6ec7-4b5b-8c32-51a630503bae}";
        Assert.Equal(endpoint, EqualizerApoApoDetector.EndpointKeyFromDeviceId($"{{0.0.0.00000000}}.{endpoint}"));
        Assert.Null(EqualizerApoApoDetector.EndpointKeyFromDeviceId(null));
        Assert.Null(EqualizerApoApoDetector.EndpointKeyFromDeviceId("Speakers"));
        Assert.Null(EqualizerApoApoDetector.EndpointKeyFromDeviceId("{0.0.0.00000000}.not-a-guid"));
    }

    [Fact]
    public void RecognizesKnownEqualizerApoClsidsOnly()
    {
        Assert.True(EqualizerApoApoDetector.ReferencesApo("{EACD2258-FCAC-4FF4-B36D-419E924A6D79}"));
        Assert.True(EqualizerApoApoDetector.ReferencesApo(
            "prefix {ec1cc9ce-faed-4822-828a-82a81a6f018f} suffix"));
        Assert.False(EqualizerApoApoDetector.ReferencesApo("{00000000-0000-0000-0000-000000000000}"));
        Assert.False(EqualizerApoApoDetector.ReferencesApo(null));
    }

    [Fact]
    public void AccessGrantRejectsMissingConfigFolder()
    {
        using var root = new TempDirectory();
        var configDirectory = root.Subdirectory("config");
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;

        var result = EqualizerApoAccessGrant.Grant(configDirectory, sid);

        Assert.False(result.Success);
        Assert.Contains("config.txt", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AccessGrantRejectsInvalidUserSid()
    {
        using var root = new TempDirectory();
        var configDirectory = root.Subdirectory("config");
        File.WriteAllText(Path.Combine(configDirectory, "config.txt"), "Preamp: 0 dB\n");

        var result = EqualizerApoAccessGrant.Grant(configDirectory, "not-a-sid");

        Assert.False(result.Success);
        Assert.Contains("user", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(configDirectory, EqualizerApoConfigStore.ManagedFileName)));
    }
}
