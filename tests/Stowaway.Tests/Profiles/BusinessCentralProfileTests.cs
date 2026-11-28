using Stowaway.Core.Inventory;
using Stowaway.Core.Profiles;

namespace Stowaway.Tests.Profiles;

public class BusinessCentralProfileTests
{
    private static readonly Dictionary<string, string> Labels = new()
    {
        ["maintainer"] = "Dynamics SMB",
        ["nav"] = "1810",
        ["version"] = "13.18.41909.0",
        ["country"] = "w1",
        ["cu"] = "cu18",
    };

    private static RawContainer Container(IReadOnlyDictionary<string, string> labels) => new(
        "id-bc", "bc-demo", "mcr.microsoft.com/businesscentral:ltsc2025", "img-bc", "exited", "Exited (0) 1 hour ago", "healthy",
        DateTimeOffset.UnixEpoch, labels,
        [
            new RawMount("bind", null, @"C:\bcartifacts.cache", @"c:\dl", true),
            new RawMount("bind", null, @"C:\windows\system32\drivers\etc", @"c:\driversetc", true),
            new RawMount("bind", null, @"C:\ProgramData\BcContainerHelper", @"c:\programdata\bccontainerhelper", true),
            new RawMount("bind", null, @"C:\ProgramData\BcContainerHelper\Extensions\bc-demo\my", @"c:\run\my", true),
        ]);

    [Fact]
    public void Business_Central_is_recognized_from_its_labels()
    {
        var profile = new BusinessCentralProfile();
        Assert.Equal(new AppProduct("business-central", "Business Central", "13.18.41909.0", "W1", BusinessCentralProfile.CardNote), profile.Detect(Labels));
        Assert.Equal("Dynamics NAV", profile.Detect(new Dictionary<string, string>(Labels) { ["version"] = "11.0.20783.0" })?.Name);
        Assert.Null(profile.Detect(new Dictionary<string, string> { ["maintainer"] = "someone else", ["version"] = "13.0" }));
        Assert.Null(profile.Detect(new Dictionary<string, string> { ["maintainer"] = "Dynamics SMB" }));
    }

    [Fact]
    public void The_containers_own_folder_is_included_and_the_shared_ones_explained()
    {
        var raw = new RawInventory([Container(Labels)], [], []);
        var sizes = new SizeIndex(new Dictionary<string, long>(), new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { [@"C:\ProgramData\BcContainerHelper\Extensions\bc-demo\my"] = 4096 });
        var app = InventoryBuilder.Build(raw, sizes, 1, DateTimeOffset.UnixEpoch, platform: Platforms.Windows).Apps.Single();
        var container = app.Containers.Single();
        Assert.Equal("Business Central", container.Product?.Name);
        Assert.Null(container.Health); // stopped: the last health check result means nothing

        var mounts = container.Mounts.ToDictionary(m => m.Destination, StringComparer.OrdinalIgnoreCase);
        var my = mounts[@"c:\run\my"];
        Assert.Equal((true, false, false, BusinessCentralProfile.MyFolderNote, 4096L), (my.Included, my.CanInclude, my.IncludedByOwner, my.Note, my.SizeBytes!.Value));

        Assert.Equal((false, true, true, BusinessCentralProfile.ArtifactCache),
            (mounts[@"c:\dl"].Included, mounts[@"c:\dl"].CanInclude, mounts[@"c:\dl"].NotNeeded, mounts[@"c:\dl"].ExclusionReason));
        var shared = mounts[@"c:\programdata\bccontainerhelper"];
        Assert.Equal((false, true, true, BusinessCentralProfile.SharedFolder), (shared.Included, shared.CanInclude, shared.NotNeeded, shared.ExclusionReason));

        // The hosts file: explained and marked not needed, but a profile still can't include a system folder.
        var hosts = mounts[@"c:\driversetc"];
        Assert.Equal((false, false, true, BusinessCentralProfile.HostsFile), (hosts.Included, hosts.CanInclude, hosts.NotNeeded, hosts.ExclusionReason));
        Assert.False(my.NotNeeded);

        // The owner can still include the cache.
        var included = InventoryBuilder.Build(raw, sizes, 1, DateTimeOffset.UnixEpoch, platform: Platforms.Windows, includedFolders: IncludedFolders.Of([@"C:\bcartifacts.cache"]));
        var cache = included.Apps.Single().Containers.Single().Mounts.Single(m => m.Destination == @"c:\dl");
        Assert.Equal((true, true, (string?)null), (cache.Included, cache.IncludedByOwner, cache.ExclusionReason));
    }

    /// <summary>The included my folder isn't where the database lives: it's still unmeasured until the first backup.</summary>
    [Fact]
    public void The_database_still_counts_when_the_containers_own_folder_is_included()
    {
        var raw = new RawInventory([Container(Labels)], [], []);
        var sizes = new SizeIndex(new Dictionary<string, long>(), new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { [@"C:\ProgramData\BcContainerHelper\Extensions\bc-demo\my"] = 4096 });
        var support = new Stowaway.Core.Databases.DatabaseSupport([(DatabaseKind.SqlServer, Platforms.Windows)]);

        var before = InventoryBuilder.Build(raw, sizes, 1, DateTimeOffset.UnixEpoch, platform: Platforms.Windows, databases: support).Apps.Single();
        Assert.Equal((4096L, true, (long?)null), (before.DataBytes, before.SizeIncomplete, before.DatabaseBytes));

        var measured = sizes with { Databases = new Dictionary<string, long> { ["container:bc-demo"] = 1_000_000 } };
        var after = InventoryBuilder.Build(raw, measured, 1, DateTimeOffset.UnixEpoch, platform: Platforms.Windows, databases: support).Apps.Single();
        Assert.Equal((1_004_096L, false, (long?)1_000_000), (after.DataBytes, after.SizeIncomplete, after.DatabaseBytes));
    }

    [Fact]
    public void Other_containers_mapping_c_run_my_get_no_special_treatment()
    {
        var raw = new RawInventory([Container(new Dictionary<string, string>())], [], []);
        var container = InventoryBuilder.Build(raw, SizeIndex.Empty, 1, DateTimeOffset.UnixEpoch, platform: Platforms.Windows).Apps.Single().Containers.Single();
        Assert.Null(container.Product);
        Assert.All(container.Mounts, m => Assert.False(m.Included));
        Assert.Equal(HostPaths.OutsideStandalone, container.Mounts.Single(m => m.Destination == @"c:\dl").ExclusionReason);
    }

    private static readonly Dictionary<string, string> Env = new()
    {
        ["artifactUrl"] = "https://bcartifacts.example.net/onprem/13.18.41909.0/w1",
        ["auth"] = "NavUserPassword",
        ["locale"] = "en-US",
        ["useSSL"] = "N",
        ["enableSymbolLoading"] = "Y",
        ["enableApiServices"] = "Y",
        ["securePassword"] = "secret-value",
        ["passwordKeyFile"] = @"c:\run\my\aes.key",
        ["licenseFile"] = @"c:\licenses\secret.flf",
        ["username"] = "admin-user",
    };

    [Fact]
    public void The_restore_script_recreates_the_container_from_its_settings_without_secrets()
    {
        var script = BusinessCentralScript.Build(new ContainerDefinition("bc-demo", Env, 8L * 1024 * 1024 * 1024, ["8.8.8.8"], [443, 80],
        [
            (@"C:\ProgramData\BcContainerHelper\Extensions\bc-demo\my", @"c:\run\my"),
            (@"C:\bcartifacts.cache", @"c:\dl"),
            (@"D:\exports", @"c:\exports"),
        ]), DateTimeOffset.UnixEpoch);

        Assert.NotNull(script);
        Assert.Contains("artifactUrl   = 'https://bcartifacts.example.net/onprem/13.18.41909.0/w1'", script);
        Assert.Contains("auth          = 'NavUserPassword'", script);
        Assert.Contains("$parameters.locale = 'en-US'", script);
        Assert.Contains("$parameters.enableSymbolLoading = $true", script);
        Assert.DoesNotContain("useSSL", script);
        Assert.Contains("$parameters.memoryLimit = '8G'", script);
        Assert.Contains("$parameters.dns = '8.8.8.8'", script);
        Assert.Contains("$parameters.publishPorts = @(80, 443)", script);
        Assert.Contains("$parameters.additionalParameters = @('--env enableApiServices=Y', '--volume \"D:\\exports:c:\\exports\"')", script);
        Assert.Contains("[string] $ContainerName = 'bc-demo'", script);
        Assert.Contains("The original container had a license file", script);
        foreach (var secret in (string[])["secret-value", "aes.key", "secret.flf", "admin-user"])
        {
            Assert.DoesNotContain(secret, script);
        }

        Assert.Equal("business-central/bc-demo.ps1", BusinessCentralScript.PathOf("bc-demo"));
    }

    [Fact]
    public void Insider_artifact_tokens_stay_out_of_the_script()
    {
        var env = new Dictionary<string, string>(Env) { ["artifactUrl"] = "https://bcinsider.example.net/sandbox/27.0.1/us?sv=2024&sig=topsecret" };
        var script = BusinessCentralScript.Build(new ContainerDefinition("bc-demo", env), DateTimeOffset.UnixEpoch)!;
        Assert.DoesNotContain("topsecret", script);
        Assert.Contains("'https://bcinsider.example.net/sandbox/27.0.1/us'", script);
        Assert.Contains("$parameters.sasToken = $SasToken", script);
        Assert.Contains("[Parameter(Mandatory = $true)] [string] $SasToken,", script);
    }

    [Fact]
    public void Without_an_artifact_url_there_is_no_script()
    {
        Assert.Null(BusinessCentralScript.Build(new ContainerDefinition("bc-demo", new Dictionary<string, string> { ["auth"] = "Windows" }), DateTimeOffset.UnixEpoch));
    }
}
