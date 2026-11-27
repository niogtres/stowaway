using Stowaway.Core.Backups;
using Stowaway.Core.Inventory;
using Stowaway.Docker.Backups;

namespace Stowaway.Tests.Backups;

/// <summary>Windows-container engines: restic runs on the host and snapshots record host paths.</summary>
public sealed class WindowsLayoutTests
{
    [Theory]
    [InlineData(@"C:\ProgramData\docker\volumes\v\_data", "/C/ProgramData/docker/volumes/v/_data")]
    [InlineData(@"c:\programdata\bccontainerhelper\", "/C/programdata/bccontainerhelper")]
    [InlineData(@"D:\", "/D")]
    public void Windows_paths_map_to_resticss_form_and_back(string windows, string restic)
    {
        Assert.Equal(restic, BackupPlanner.WindowsResticPath(windows));
        Assert.Equal(windows.TrimEnd('\\').Length <= 2 ? @"D:\" : char.ToUpperInvariant(windows[0]) + windows[1..].TrimEnd('\\'),
            BackupPlanner.FromWindowsResticPath(restic));
    }

    [Theory]
    [InlineData("/data/app/x")]
    [InlineData("/repo")]
    [InlineData("/run/stowaway/env")]
    public void Container_paths_are_not_windows_paths(string path) => Assert.Null(BackupPlanner.FromWindowsResticPath(path));

    [Fact]
    public void Windows_apps_are_laid_out_under_their_host_paths()
    {
        var app = new AppInfo
        {
            Id = "container:sql", Name = "sql", Kind = AppKind.Standalone, Protection = ProtectionStatus.Unprotected, Platform = Platforms.Windows,
            Containers =
            [
                new ContainerInfo
                {
                    Id = "1", Name = "sql", Image = "img", ImageId = "i", State = "running", StatusText = "Up", CreatedAt = DateTimeOffset.UnixEpoch,
                    Mounts =
                    [
                        new MountInfo { Kind = MountKind.Volume, Source = @"C:\ProgramData\docker\volumes\sqldata\_data", DisplaySource = "sqldata", VolumeName = "sqldata", Destination = @"c:\data", Included = true },
                        new MountInfo { Kind = MountKind.Bind, Source = @"c:\apps\sql\conf", DisplaySource = @"C:\apps\sql\conf", Destination = @"c:\conf", Included = true },
                    ],
                },
            ],
        };

        var layout = BackupPlanner.Layout(app);
        Assert.Equal(["/C/ProgramData/docker/volumes/sqldata/_data", "/C/apps/sql/conf"], layout.Select(s => s.SnapshotPath));
        Assert.Equal("sqldata", layout[0].Source); // restores still target the volume by name

        // The same app on a Linux engine keeps the /data layout.
        Assert.All(BackupPlanner.Layout(app with { Platform = Platforms.Linux }), s => Assert.StartsWith("/data/sql/", s.SnapshotPath));
    }

    [Fact]
    public void Metadata_and_dumps_are_found_from_the_snapshots_paths()
    {
        Assert.Equal("/data/_stowaway", BackupPlanner.MetadataDirOf(["/data"]));
        Assert.Equal("/data/_dumps", BackupPlanner.DumpsDirOf(null));
        IReadOnlyList<string> windows = ["/C/ProgramData/docker/volumes/v/_data", "/C/ProgramData/Stowaway/stowaway-stage"];
        Assert.Equal("/C/ProgramData/Stowaway/stowaway-stage/_stowaway", BackupPlanner.MetadataDirOf(windows));
        Assert.Equal("/C/ProgramData/Stowaway/stowaway-stage/_dumps", BackupPlanner.DumpsDirOf(windows));
    }

    [Fact]
    public void Container_paths_translate_to_host_paths()
    {
        var map = new HostPathMap();
        map.Add("/repo", @"\\nas\backups\stowaway");
        map.Add("/restore", @"C:\ProgramData\docker\volumes\new\_data");
        map.Add("/run", @"C:\ProgramData\Stowaway\work\abc\run");

        Assert.Equal(@"\\nas\backups\stowaway", map.Translate("/repo"));
        Assert.Equal(@"C:\ProgramData\Stowaway\work\abc\run\stowaway\repository-password", map.Translate("/run/stowaway/repository-password"));
        Assert.Null(map.Translate("/repository")); // a prefix only matches whole path segments
        Assert.Null(map.Translate("/data/app/x")); // snapshot paths aren't touched

        // Arguments: whole paths are translated; key files inside option strings become forward-slash host paths.
        Assert.Equal(@"C:\ProgramData\docker\volumes\new\_data", map.TranslateValue("/restore", "/run/stowaway"));
        Assert.Equal("abc123:/C/ProgramData/docker/volumes/v/_data", map.TranslateValue("abc123:/C/ProgramData/docker/volumes/v/_data", "/run/stowaway"));
        Assert.Equal("sftp.command=ssh -i C:/ProgramData/Stowaway/work/abc/run/stowaway/id_key host",
            map.TranslateValue("sftp.command=ssh -i /run/stowaway/id_key host", "/run/stowaway"));
    }

    [Theory]
    [InlineData(@"c:\windows\system32\drivers\etc")]
    [InlineData(@"C:\Program Files\App")]
    public void Windows_system_folders_are_plumbing(string source) =>
        Assert.StartsWith("System folder", HostPaths.BindExclusionReason(source, @"c:\x", projectDir: null));

    [Fact]
    public void Snapshot_paths_from_windows_restic_use_the_tree_form()
    {
        const string json = """{"id":"abc","time":"2026-11-27T10:00:00Z","paths":["C:\\ProgramData\\docker\\volumes\\v\\_data","C:\\data\\stowaway-stage","/data"]}""";
        var snapshot = System.Text.Json.JsonSerializer.Deserialize<Stowaway.Core.Restic.ResticSnapshot>(json)!;
        Assert.Equal(["/C/ProgramData/docker/volumes/v/_data", "/C/data/stowaway-stage", "/data"], snapshot.Paths!);
        Assert.Equal("/C/data/stowaway-stage/_stowaway", BackupPlanner.MetadataDirOf(snapshot.Paths));
    }
}
