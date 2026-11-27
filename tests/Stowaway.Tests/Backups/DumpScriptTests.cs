using Stowaway.Core.Backups;
using Stowaway.Core.Inventory;

namespace Stowaway.Tests.Backups;

public sealed class DumpScriptTests
{
    [Theory]
    [InlineData(DatabaseKind.PostgreSql)]
    [InlineData(DatabaseKind.MySql)]
    [InlineData(DatabaseKind.MariaDb)]
    [InlineData(DatabaseKind.MongoDb)]
    [InlineData(DatabaseKind.Redis)]
    public void Dump_scripts_have_unix_line_endings_whatever_the_checkout(DatabaseKind kind) =>
        Assert.DoesNotContain('\r', DatabaseDumps.Script(kind));
}
