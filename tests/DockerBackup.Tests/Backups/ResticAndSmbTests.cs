using DockerBackup.Core.Backups;
using DockerBackup.Core.Restic;
using DockerBackup.Docker.Backups;

namespace DockerBackup.Tests.Backups;

public sealed class ResticOutputParserTests
{
    // Lines captured from restic 0.19.1.
    [Fact]
    public void Parses_status_lines()
    {
        var status = Assert.IsType<ResticStatus>(ResticOutputParser.Parse(
            """{"message_type":"status","percent_done":0.5,"total_files":2,"files_done":1,"total_bytes":3000006,"bytes_done":1500003}"""));
        Assert.Equal(0.5, status.PercentDone);
        Assert.Equal(3000006, status.TotalBytes);
        Assert.Equal(1500003, status.BytesDone);
    }

    [Fact]
    public void Parses_summary()
    {
        var summary = Assert.IsType<ResticSummary>(ResticOutputParser.Parse(
            """{"message_type":"summary","files_new":2,"files_changed":0,"files_unmodified":0,"dirs_new":4,"data_added":3002176,"total_files_processed":2,"total_bytes_processed":3000006,"total_duration":0.846,"snapshot_id":"3caee76dfd1cb95ee13a58e9690864f6654331ac6da905b7b21e7368a05d2ec6"}"""));
        Assert.StartsWith("3caee76d", summary.SnapshotId);
        Assert.Equal(3002176, summary.DataAdded);
        Assert.Equal(2, summary.TotalFilesProcessed);
    }

    [Fact]
    public void Parses_errors_init_and_text()
    {
        var error = Assert.IsType<ResticExitError>(ResticOutputParser.Parse("""{"message_type":"exit_error","code":12,"message":"Fatal: wrong password or no key found"}"""));
        Assert.Equal(12, error.Code);

        var init = Assert.IsType<ResticInitialized>(ResticOutputParser.Parse("""{"message_type":"initialized","id":"929a06d1","repository":"/repo"}"""));
        Assert.Equal("929a06d1", init.RepositoryId);

        Assert.IsType<ResticText>(ResticOutputParser.Parse("unable to open cache: permission denied"));
        Assert.IsType<ResticText>(ResticOutputParser.Parse("{not json"));
    }

    [Fact]
    public void Parses_snapshots_and_stats()
    {
        var snapshots = ResticOutputParser.ParseSnapshots(
            """[{"time":"2026-11-27T08:28:51.513632079Z","tree":"7c","paths":["/data"],"hostname":"testhost","tags":["app=x"],"id":"3caee76d","short_id":"3caee76d"}]""");
        var snapshot = Assert.Single(snapshots);
        Assert.Equal(["app=x"], snapshot.Tags);
        Assert.Equal(2026, snapshot.Time.Year);

        var stats = ResticOutputParser.ParseStats("""{"total_size":3002088,"total_uncompressed_size":3003197,"compression_ratio":1.0003,"snapshots_count":2}""");
        Assert.Equal(3002088, stats!.TotalSize);
        Assert.Equal(2, stats.SnapshotsCount);
    }

    [Fact]
    public void Error_messages_prefer_restic_json_and_friendly_codes()
    {
        var wrong = new RunnerResult(12, [], ["""{"message_type":"exit_error","code":12,"message":"Fatal: wrong password or no key found"}"""]);
        Assert.Equal("Wrong repository password", ResticRepository.ErrorMessage(wrong));

        var other = new RunnerResult(1, [], ["""{"message_type":"exit_error","code":1,"message":"Fatal: unable to open config file: Stat: stat /repo/config: no such file or directory"}"""]);
        Assert.StartsWith("unable to open config file", ResticRepository.ErrorMessage(other));
    }
}

public sealed class SmbMountTests
{
    [Theory]
    [InlineData(@"\\nas\backups\docker\host1", "nas", "backups", "docker/host1")]
    [InlineData(@"\\nas.local\share", "nas.local", "share", "")]
    [InlineData("//10.0.0.5/data/x", "10.0.0.5", "data", "x")]
    public void Parses_unc_paths(string path, string server, string share, string sub) =>
        Assert.Equal((server, share, sub), SmbMount.ParseUnc(path));

    [Theory]
    [InlineData(@"D:\Backups")]
    [InlineData(@"\\nas")]
    [InlineData(@"\\.\pipe\docker_engine")]
    public void Rejects_non_share_paths(string path) => Assert.Null(SmbMount.ParseUnc(path));

    [Fact]
    public void Builds_cifs_volume_options_with_escaped_credentials()
    {
        var options = SmbMount.VolumeOptions(@"\\nas\backups\docker", "backup", "p,ss", "WORKGROUP");
        Assert.Equal("cifs", options["type"]);
        Assert.Equal("//nas/backups/docker", options["device"]);
        Assert.Contains("addr=nas", options["o"]);
        Assert.Contains("username=backup", options["o"]);
        Assert.Contains("password=p,,ss", options["o"]);
        Assert.Contains("domain=WORKGROUP", options["o"]);
        Assert.Contains("vers=3.0", options["o"]);
    }

    [Fact]
    public void Uses_guest_access_without_username()
    {
        var options = SmbMount.VolumeOptions(@"\\nas\public", null, null, null);
        Assert.Contains("guest", options["o"]);
        Assert.DoesNotContain("password=", options["o"]);
    }
}

public sealed class DestinationValidationTests
{
    [Theory]
    [InlineData(DestinationKind.Local, @"D:\Backups\docker\", @"D:\Backups\docker")]
    [InlineData(DestinationKind.Local, "e:/backups", @"e:\backups")]
    [InlineData(DestinationKind.Smb, "//nas/backups/docker", @"\\nas\backups\docker")]
    public void Normalizes_valid_paths(DestinationKind kind, string input, string expected) =>
        Assert.Equal(expected, DestinationService.ValidatePath(kind, input));

    [Theory]
    [InlineData(DestinationKind.Local, "relative\\path")]
    [InlineData(DestinationKind.Local, @"D:\")]
    [InlineData(DestinationKind.Local, @"\\nas\share")]
    [InlineData(DestinationKind.Smb, @"D:\Backups")]
    public void Rejects_invalid_paths(DestinationKind kind, string input) =>
        Assert.Throws<ValidationException>(() => DestinationService.ValidatePath(kind, input));
}
