using Microsoft.Extensions.DependencyInjection;
using Stowaway.Core.Databases;
using Stowaway.Core.Inventory;
using Stowaway.Docker.Databases;

namespace Stowaway.Tests.Backups;

/// <summary>The providers as the real service registers them, and rules every shell definition must follow.</summary>
public sealed class DatabaseProviderTests(SignInFactory factory) : IClassFixture<SignInFactory>
{
    [Fact]
    public void The_service_registers_the_built_in_providers()
    {
        var providers = factory.Services.GetRequiredService<DatabaseProviders>();
        foreach (var kind in new[] { DatabaseKind.PostgreSql, DatabaseKind.MySql, DatabaseKind.MariaDb, DatabaseKind.MongoDb, DatabaseKind.Redis })
        {
            Assert.True(providers.Support.CanDump(kind, Platforms.Linux), $"{kind} on Linux");
            Assert.IsType<ShellDatabaseProvider>(providers.For(kind, Platforms.Linux));
        }

        Assert.IsType<SqlServerProvider>(providers.For(DatabaseKind.SqlServer, Platforms.Windows));
        Assert.Null(providers.For(DatabaseKind.Elasticsearch, Platforms.Linux));
        Assert.Same(providers.Support, factory.Services.GetRequiredService<DatabaseSupport>()); // what the inventory uses
    }

    public static TheoryData<int> Definitions => [.. Enumerable.Range(0, ShellDatabases.All.Count)];

    [Theory]
    [MemberData(nameof(Definitions))]
    public void Every_shell_definition_is_complete(int index)
    {
        var definition = ShellDatabases.All[index];
        Assert.NotEmpty(definition.Kinds);
        Assert.Matches("^[a-z0-9]+$", definition.Extension);
        Assert.DoesNotContain('\r', definition.DumpScript);
        Assert.DoesNotContain('\r', definition.ProbeCommand[^1]);
        Assert.Equal(["sh", "-c"], definition.ProbeCommand.Take(2));

        // The drill must reach the dump where the drill container mounts it, and print one number.
        var check = definition.DrillFor("x.dump");
        Assert.Contains("/drill/x.dump", string.Join(' ', (check.Load ?? check.CommandOverride)!));
        Assert.NotEmpty(check.Ready);
        Assert.NotEmpty(check.Probe);
        Assert.False(string.IsNullOrWhiteSpace(check.ProbeLabel));
    }

    [Fact]
    public void No_kind_has_two_shell_definitions() =>
        Assert.Equal(ShellDatabases.All.SelectMany(d => d.Kinds).Count(), ShellDatabases.All.SelectMany(d => d.Kinds).Distinct().Count());
}
