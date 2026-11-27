using System.Text;
using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Stowaway.Core.Backups;
using Stowaway.Core.Inventory;
using Stowaway.Docker;
using Stowaway.Docker.Backups;
using Stowaway.Docker.Restore;

namespace Stowaway.Tests.Backups;

/// <summary>
/// SQL Server in a Windows container, end to end: backup while it runs, file copy while it's stopped, and a drill of
/// each. Opt-in, because it needs a large image with SQL Server inside (e.g. Business Central): set
/// <c>STOWAWAY_TEST_SQL_IMAGE</c> to an image that is already on the machine. Run elevated (it reads volume folders).
/// Only throwaway containers and volumes named stowaway-wintest-*; the image's own startup never runs.
/// </summary>
public sealed class SqlServerWindowsTests
{
    [Fact]
    public async Task Backs_up_a_running_and_a_stopped_sql_server_container_and_drills_both()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = Environment.GetEnvironmentVariable("STOWAWAY_TEST_SQL_IMAGE");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(image), "Set STOWAWAY_TEST_SQL_IMAGE to a local Windows image with SQL Server (e.g. Business Central)");
        var monitor = new DockerEngineMonitor(Options.Create(new DockerEngineOptions()), TimeProvider.System, NullLogger<DockerEngineMonitor>.Instance);
        var status = await monitor.ProbeAsync(ct);
        Assert.SkipUnless(status.Reachable && status.OsType == "windows", "Needs a Windows-container engine");

        using var client = new DockerClientBuilder().WithEndpoint(new Uri(status.Endpoint!)).WithTimeout(TimeSpan.FromMinutes(10)).Build();
        Assert.True(await ContainerRecreator.ImageExistsAsync(client, image!, ct), $"{image} isn't on this machine");

        var id = Guid.NewGuid().ToString("N")[..8];
        var name = $"stowaway-wintest-sql-{id}";
        var work = Path.Combine(Path.GetTempPath(), $"stowaway-wintest-{id}");
        var volumes = new List<string>();
        string? containerId = null;
        var log = new List<string>();
        try
        {
            // SQL Server only, exactly as the drill starts it; then a small database in C:\databases.
            var created = await client.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Name = name,
                Image = image,
                Entrypoint = ["powershell.exe", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(SqlServerDrill.StartScript))],
                HostConfig = new HostConfig { Memory = 4L * 1024 * 1024 * 1024 },
            }, ct);
            containerId = created.ID;
            await client.Containers.StartContainerAsync(containerId, new ContainerStartParameters(), ct);
            var instance = await WaitForSqlAsync(client, containerId, ct);
            await ExecAsync(client, containerId, ["cmd.exe", "/c", "mkdir", @"C:\databases"], ct);
            await SqlAsync(client, containerId, instance,
                @"CREATE DATABASE [stowaway_test] ON (NAME = stowaway_test, FILENAME = 'C:\databases\stowaway_test.mdf') LOG ON (NAME = stowaway_test_log, FILENAME = 'C:\databases\stowaway_test_log.ldf')", ct);
            await SqlAsync(client, containerId, instance, "CREATE TABLE [stowaway_test].dbo.t (id int); INSERT INTO [stowaway_test].dbo.t VALUES (1), (2), (3)", ct);

            var container = new ContainerInfo
            {
                Id = containerId, Name = name, Image = image!, ImageId = "", State = "running", StatusText = "Up", Mounts = [], Database = DatabaseKind.SqlServer,
            };
            var app = new AppInfo { Id = name, Name = name, Kind = AppKind.Standalone, Platform = Platforms.Windows, Protection = ProtectionStatus.Unprotected, Containers = [container] };
            var options = Options.Create(new BackupOptions { HostWorkDirectory = work });
            var runner = new ResticRunner(monitor, options, NullLogger<ResticRunner>.Instance);
            var service = new SqlServerDumpService(runner, options, NullLogger<SqlServerDumpService>.Instance);
            var folder = Path.Combine(runner.HostStageDirectory, BackupPlanner.DumpsFolder, SqlServerDumps.FolderName(name));

            // 1. Running: BACKUP DATABASE + tar out.
            runner.ClearHostStage();
            Assert.Empty(await service.CreateAsync(client, app, log.Add, ct));
            var running = Manifest(folder);
            Assert.Equal(SqlServerDumps.BackupMode, running.Mode);
            var database = Assert.Single(running.Databases);
            Assert.Equal("stowaway_test", database.Name);
            Assert.True(new FileInfo(Path.Combine(folder, database.BackupFile!)).Length > 0);
            await DrillAsync(client, runner, volumes, $"stowaway-wintest-{id}-drill1", log, ct);

            // 2. Stopped: database files copied out through the archive API, without starting it.
            await client.Containers.StopContainerAsync(containerId, new ContainerStopParameters { WaitBeforeKillSeconds = 60 }, ct);
            runner.ClearHostStage();
            Assert.Empty(await service.CreateAsync(client, app with { Containers = [container with { State = "exited" }] }, log.Add, ct));
            Assert.False((await client.Containers.InspectContainerAsync(containerId, ct)).State?.Running == true, "copying files must not start the container");
            var stopped = Manifest(folder);
            Assert.Equal(SqlServerDumps.FilesMode, stopped.Mode);
            Assert.Contains(@"C:\databases\stowaway_test.mdf", stopped.Files, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(@"C:\databases\stowaway_test_log.ldf", stopped.Files, StringComparer.OrdinalIgnoreCase);
            await DrillAsync(client, runner, volumes, $"stowaway-wintest-{id}-drill2", log, ct);
        }
        finally
        {
            TestContext.Current.SendDiagnosticMessage(string.Join('\n', log));
            if (containerId is not null)
            {
                await Ignore(() => client.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true }, CancellationToken.None));
            }

            foreach (var volume in volumes)
            {
                await Ignore(() => client.Volumes.RemoveAsync(volume, force: true, CancellationToken.None));
            }

            if (Directory.Exists(work))
            {
                Directory.Delete(work, recursive: true);
            }
        }
    }

    /// <summary>Puts the staged _dumps into a volume, as a drill's restore would, and runs the SQL Server drill on it.</summary>
    private static async Task DrillAsync(DockerClient client, ResticRunner runner, List<string> volumes, string volume, List<string> log, CancellationToken ct)
    {
        await client.Volumes.CreateAsync(new VolumesCreateParameters { Name = volume }, ct);
        volumes.Add(volume);
        var target = (await client.Volumes.InspectAsync(volume, ct)).Mountpoint;
        var source = Path.Combine(runner.HostStageDirectory, BackupPlanner.DumpsFolder);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var copy = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }

        var failures = new List<string>();
        await new SqlServerDrill(NullLogger<SqlServerDrill>.Instance).CheckAsync(client, volume, log.Add, _ => { }, failures, ct);
        Assert.True(failures.Count == 0, string.Join('\n', failures.Concat(log)));
        Assert.Contains(log, l => l.Contains("DBCC CHECKDB clean", StringComparison.Ordinal) && l.Contains("stowaway_test", StringComparison.Ordinal));
    }

    private static SqlServerManifest Manifest(string folder) =>
        JsonSerializer.Deserialize<SqlServerManifest>(File.ReadAllBytes(Path.Combine(folder, SqlServerDumps.ManifestName)))!;

    private static async Task<string> WaitForSqlAsync(DockerClient client, string id, CancellationToken ct)
    {
        for (var i = 0; i < 90; i++)
        {
            foreach (var instance in SqlServerDumps.Instances)
            {
                if ((await DockerExec.RunAsync(client, id, SqlServerDumps.Sqlcmd(instance, "SELECT 1", null, 5), ct, 4096, windows: true)).ExitCode == 0)
                {
                    return instance;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        throw new TimeoutException("SQL Server didn't start");
    }

    private static Task SqlAsync(DockerClient client, string id, string instance, string query, CancellationToken ct) =>
        ExecAsync(client, id, SqlServerDumps.Sqlcmd(instance, query, null), ct);

    private static async Task ExecAsync(DockerClient client, string id, List<string> command, CancellationToken ct)
    {
        var result = await DockerExec.RunAsync(client, id, command, ct, 64 * 1024, windows: true);
        Assert.True(result.ExitCode == 0, $"{string.Join(' ', command)} → {result.ExitCode}: {result.StdOut} {result.StdErr}");
    }

    private static async Task Ignore(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception)
        {
            // best-effort cleanup
        }
    }
}
