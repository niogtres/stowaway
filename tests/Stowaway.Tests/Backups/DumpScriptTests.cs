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

    [Fact]
    public void Commands_report_their_own_exit_code_on_stderr()
    {
        var wrapped = Stowaway.Docker.DockerExec.WithExitCode(["pg_dumpall", "-U", "postgres"]);
        Assert.Equal(["sh", "-c"], wrapped[..2]);
        Assert.Equal(["sh", "pg_dumpall", "-U", "postgres"], wrapped[3..]);

        var stderr = new System.Text.StringBuilder("warning: something\n__stowaway_exit=2\n");
        Assert.Equal(2, Stowaway.Docker.DockerExec.TakeExitCode(stderr));
        Assert.Equal("warning: something\n", stderr.ToString());
        Assert.Null(Stowaway.Docker.DockerExec.TakeExitCode(new System.Text.StringBuilder("no marker")));
    }
}
