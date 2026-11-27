using Microsoft.Extensions.Logging.Abstractions;
using Stowaway.Core.Databases;
using Stowaway.Docker.Backups;
using Stowaway.Docker.Databases;

namespace Stowaway.Tests.Backups;

/// <summary>The built-in database providers, for tests that don't run the whole service.</summary>
internal static class TestDatabases
{
    public static DatabaseProviders Providers(DumpService? dumps = null) => new(
    [
        new ShellDatabaseProvider(ShellDatabases.All, dumps!, NullLogger<ShellDatabaseProvider>.Instance),
        new SqlServerProvider(null!, null!),
    ]);

    public static DatabaseSupport Support { get; } = Providers().Support;
}
