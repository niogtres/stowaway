using Stowaway.Core.Inventory;
using Stowaway.Core.Profiles;

namespace Stowaway.Tests.Profiles;

/// <summary>Rules every built-in app profile must keep (docs/PROVIDERS.md, App profiles).</summary>
public class AppProfileTests
{
    public static TheoryData<string> Profiles() => [.. AppProfiles.BuiltIn.Select(p => p.Id)];

    [Fact]
    public void Profile_ids_are_unique()
    {
        Assert.Equal(AppProfiles.BuiltIn.Count, AppProfiles.BuiltIn.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Containers_without_labels_have_no_product()
    {
        Assert.All(AppProfiles.BuiltIn, p => Assert.Null(p.Detect(new Dictionary<string, string>())));
    }

    /// <summary>A profile that wants everything still can't include drives, system folders or shares.</summary>
    [Fact]
    public void Profiles_only_decide_about_folders_the_owner_could_include()
    {
        var greedy = new GreedyProfile();
        var mounts = new[]
        {
            new RawMount("bind", null, @"M:\", @"c:\media", true),
            new RawMount("bind", null, @"C:\Windows\System32\drivers\etc", @"c:\driversetc", true),
            new RawMount("bind", null, @"\nas\share", @"c:\share", true),
            new RawMount("bind", null, @"C:\data\app", @"c:\data", true),
        };

        var decisions = mounts.Select(m => (m.Destination, Decision: InventoryBuilder.DecideBind(m, null, greedy.Product, IncludedFolders.Empty, greedy))).ToList();
        Assert.Equal([@"c:\data"], decisions.Where(d => d.Decision.Reason is null).Select(d => d.Destination));
    }

    [Fact]
    public void A_product_from_a_profile_this_build_lacks_is_ignored()
    {
        Assert.Null(AppProfiles.Of(new AppProduct("retired-profile", "Something")));
    }

    private sealed class GreedyProfile : IAppProfile
    {
        public AppProduct Product { get; } = new("greedy", "Greedy");
        public string Id => "greedy";
        public string? DefinitionDescription => null;
        public AppProduct? Detect(IReadOnlyDictionary<string, string> labels) => Product;
        public (string? Reason, string? Note) Bind(string defaultReason, string windowsSource, string destination) => (null, "mine");
        public IReadOnlyDictionary<string, string> DefinitionFiles(ContainerDefinition container, DateTimeOffset backedUpAt) => new Dictionary<string, string>();
        public string? RecreateWarning(string containerName) => null;
    }
}
