using Stowaway.Core.Backups;
using Stowaway.Core.Inventory;
using Stowaway.Docker.Restore;

namespace Stowaway.Tests.Backups;

public sealed class SqlServerDumpTests
{
    [Theory]
    [InlineData("mcr.microsoft.com/businesscentral:ltsc2025")]
    [InlineData("mcr.microsoft.com/businesscentral/sandbox:23.0")]
    [InlineData("mcr.microsoft.com/dynamicsnav:2018")]
    public void Business_central_images_are_sql_server(string image) =>
        Assert.Equal(DatabaseKind.SqlServer, DatabaseDetector.Detect(image));

    [Fact]
    public void Sql_server_is_dumpable_only_on_windows_engines()
    {
        Assert.True(DatabaseDumps.Supported(DatabaseKind.SqlServer, Platforms.Windows));
        Assert.False(DatabaseDumps.Supported(DatabaseKind.SqlServer, Platforms.Linux));
        Assert.False(DatabaseDumps.Supported(DatabaseKind.PostgreSql, Platforms.Windows));
        Assert.True(DatabaseDumps.Supported(DatabaseKind.PostgreSql));
    }

    [Fact]
    public void Windows_sql_server_doesnt_force_stop_and_start_and_gets_no_shell_dump()
    {
        var app = new AppInfo
        {
            Id = "bc", Name = "bc", Kind = AppKind.Standalone, Platform = Platforms.Windows, Protection = ProtectionStatus.Unprotected,
            Containers = [new ContainerInfo { Id = "1", Name = "bc", Image = "mcr.microsoft.com/businesscentral:ltsc2025", ImageId = "sha256:1", State = "running", StatusText = "Up", Mounts = [], Database = DatabaseKind.SqlServer }],
        };

        Assert.Empty(DatabaseDumps.Undumpable(app));
        Assert.Empty(DatabaseDumps.For(app));
        Assert.Single(SqlServerDumps.Containers(app));
        Assert.Empty(SqlServerDumps.Containers(app with { Platform = Platforms.Linux }));
    }

    [Fact]
    public void Sqlcmd_uses_windows_authentication_unless_a_login_is_saved()
    {
        var windows = SqlServerDumps.Sqlcmd(@".\SQLEXPRESS", "SELECT 1", null);
        Assert.Equal(["sqlcmd", "-S", @".\SQLEXPRESS", "-E"], windows.Take(4));
        Assert.Equal("SELECT 1", windows[^1]);

        var login = SqlServerDumps.Sqlcmd(".", "SELECT 1", "backup");
        Assert.Equal(["-U", "backup"], login.Skip(3).Take(2));
        Assert.DoesNotContain(login, a => a.Contains("password", StringComparison.OrdinalIgnoreCase)); // via SQLCMDPASSWORD
    }

    [Fact]
    public void Listing_is_parsed_into_databases_with_their_files()
    {
        const string output = "Cronus|C:\\databases\\Cronus_Data.mdf\r\nCronus|C:\\databases\\Cronus_Log.ldf\r\nother|db|C:\\x\\o.mdf\r\n\r\n";
        var databases = SqlServerDumps.ParseList(output);
        Assert.Equal(2, databases.Count);
        Assert.Equal("Cronus", databases[0].Name);
        Assert.Equal([@"C:\databases\Cronus_Data.mdf", @"C:\databases\Cronus_Log.ldf"], databases[0].Files);
        Assert.Equal("other|db", databases[1].Name); // a | in the name: the path is after the last one
    }

    [Fact]
    public void Names_and_queries_are_escaped()
    {
        Assert.Equal(["a_b.bak", "a_b-2.bak", "x.y.bak"], SqlServerDumps.BackupFileNames(["a b", "a/b", "x.y"]));
        Assert.Equal(
            @"BACKUP DATABASE [we]]ird] TO DISK = N'C:\stowaway-dump\o''k.bak' WITH COPY_ONLY, INIT, CHECKSUM",
            SqlServerDumps.BackupQuery("we]ird", "o'k.bak"));
        Assert.Contains("AND d.name = N'it''s'", SqlServerDumps.ListQuery("it's"));
    }

    [Fact]
    public void Stopped_containers_copy_remembered_files_and_database_files_in_the_default_folder()
    {
        var remembered = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Program Files\Microsoft SQL Server\MSSQL16.SQLEXPRESS\MSSQL\DATA\app.mdf" };
        Assert.Equal([@"C:\Program Files\Microsoft SQL Server\MSSQL16.SQLEXPRESS\MSSQL\DATA", @"C:\databases"], SqlServerDumps.FoldersToCopy(remembered));

        Assert.True(SqlServerDumps.ShouldCopy(@"C:\Program Files\Microsoft SQL Server\MSSQL16.SQLEXPRESS\MSSQL\DATA\app.mdf", remembered));
        Assert.False(SqlServerDumps.ShouldCopy(@"C:\Program Files\Microsoft SQL Server\MSSQL16.SQLEXPRESS\MSSQL\DATA\master.mdf", remembered));
        Assert.True(SqlServerDumps.ShouldCopy(@"C:\databases\Cronus_Log.ldf", remembered));
        Assert.False(SqlServerDumps.ShouldCopy(@"C:\databases\readme.txt", remembered));
    }

    [Fact]
    public void Archive_entries_map_to_container_paths_and_to_the_dump_folder()
    {
        Assert.Equal(@"C:\databases\sub\a.mdf", SqlServerDumps.ContainerPathOfArchiveEntry(@"C:\databases", "databases/sub/a.mdf"));
        Assert.Null(SqlServerDumps.ContainerPathOfArchiveEntry(@"C:\databases", "databases/"));
        Assert.Null(SqlServerDumps.ContainerPathOfArchiveEntry(@"C:\databases", "databases/../evil.mdf"));

        Assert.Equal(Path.Combine("files", "C", "databases", "a.mdf"), SqlServerDumps.CopiedFilePath(@"c:\databases\a.mdf"));
        Assert.Null(SqlServerDumps.CopiedFilePath(@"\\server\share\a.mdf"));
        Assert.Equal(@"C:\stowaway-drill\bc\files\C\databases\a.mdf", SqlServerDrill.DrillPath(@"C:\stowaway-drill\bc", @"C:\databases\a.mdf"));
    }

    [Fact]
    public void Drill_restores_with_move_from_the_backups_own_file_list()
    {
        const string fileList = "Cronus_Data|C:\\databases\\Cronus_Data.mdf|D|PRIMARY|1\r\nCronus_Log|C:\\databases\\Cronus_Log.ldf|L|NULL|2\r\n";
        var files = SqlServerDrill.ParseFileList(fileList);
        Assert.Equal([("Cronus_Data", "D"), ("Cronus_Log", "L")], files);
        Assert.Equal(
            @"RESTORE DATABASE [stowaway_drill_0] FROM DISK = N'C:\stowaway-drill\bc\Cronus.bak' WITH CHECKSUM, MOVE N'Cronus_Data' TO N'C:\stowaway-drill-data\stowaway_drill_0_0.mdf', MOVE N'Cronus_Log' TO N'C:\stowaway-drill-data\stowaway_drill_0_1.ldf'",
            SqlServerDrill.RestoreStatement("stowaway_drill_0", @"C:\stowaway-drill\bc\Cronus.bak", files));
    }

    [Fact]
    public void Drill_attaches_every_file_the_primary_file_lists()
    {
        const string output = "2|1|Cronus_Data|C:\\databases\\Cronus_Data.mdf\r\n0|2|Cronus_Log|C:\\databases\\Cronus_Log.ldf\r\n";
        Assert.Equal([@"C:\databases\Cronus_Data.mdf", @"C:\databases\Cronus_Log.ldf"], SqlServerDrill.ParseLastColumn(output));
        Assert.Equal(
            @"CREATE DATABASE [d] ON (FILENAME = N'C:\a.mdf'), (FILENAME = N'C:\a.ldf') FOR ATTACH",
            SqlServerDrill.AttachStatement("d", [@"C:\a.mdf", @"C:\a.ldf"]));
    }

    [Fact]
    public void Drill_start_script_is_a_valid_encoded_command()
    {
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(SqlServerDrill.StartScript));
        Assert.Equal(SqlServerDrill.StartScript, System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(encoded)));
        Assert.Contains("MSSQL$*", SqlServerDrill.StartScript, StringComparison.Ordinal);
    }
}
