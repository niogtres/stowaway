using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using DockerBackup.Core.Backups;
using Microsoft.Extensions.Logging;

namespace DockerBackup.Docker.Backups;

public sealed record DumpResult(DumpCommand Dump, long Bytes, string? Error);

/// <summary>
/// Streams database dumps from inside the database containers into a temporary volume, which the backup then
/// includes. The dump runs with the database's own tools; a small "sink" runner writes the stream to disk.
/// </summary>
public sealed class DumpService(ResticRunner runner, ILogger<DumpService> logger)
{
    private const int MaxStderr = 4096;

    public static string VolumeName(Guid runId) => $"dockerbackup-dumps-{runId:N}";

    public async Task<IReadOnlyList<DumpResult>> CreateAsync(DockerClient client, IReadOnlyList<DumpCommand> dumps, Guid runId, Action<string> log, CancellationToken ct)
    {
        await runner.EnsureImageAsync(client, ct);
        await client.Volumes.CreateAsync(new VolumesCreateParameters
        {
            Name = VolumeName(runId),
            Labels = new Dictionary<string, string> { [ResticRunner.RunnerLabel] = "true" },
        }, ct);

        var results = new List<DumpResult>();
        foreach (var dump in dumps)
        {
            log($"Dumping {dump.Container.Name} ({dump.Kind})");
            var result = await DumpOneAsync(client, VolumeName(runId), dump, ct);
            log(result.Error is null
                ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Dumped {dump.Container.Name}: {result.Bytes:N0} bytes")
                : $"Dump of {dump.Container.Name} failed: {result.Error}");
            results.Add(result);
        }

        return results;
    }

    public async Task RemoveAsync(DockerClient client, Guid runId)
    {
        try
        {
            await client.Volumes.RemoveAsync(VolumeName(runId), force: true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not remove dump volume {Volume}", VolumeName(runId));
        }
    }

    private async Task<DumpResult> DumpOneAsync(DockerClient client, string volume, DumpCommand dump, CancellationToken ct)
    {
        var sink = await client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = runner.Image,
            Entrypoint = ["sh"],
            Cmd = ["-c", $"cat > /dumps/{dump.FileName}"],
            AttachStdin = true,
            OpenStdin = true,
            StdinOnce = true,
            Labels = new Dictionary<string, string> { [ResticRunner.RunnerLabel] = "true", ["dockerbackup.purpose"] = "dump" },
            HostConfig = new HostConfig
            {
                Mounts = [new Mount { Type = "volume", Source = volume, Target = "/dumps" }],
                NetworkMode = "none",
                SecurityOpt = ["no-new-privileges"],
            },
        }, ct);

        try
        {
            using var input = await client.Containers.AttachContainerAsync(sink.ID,
                new ContainerAttachParameters { Stream = true, Stdin = true, Stdout = true, Stderr = true }, ct);
            await client.Containers.StartContainerAsync(sink.ID, new ContainerStartParameters(), ct);

            var exec = await client.Exec.CreateContainerExecAsync(dump.Container.Id, new ContainerExecCreateParameters
            {
                AttachStdout = true,
                AttachStderr = true,
                Cmd = dump.Command.ToList(),
            }, ct);

            long bytes = 0;
            var stderr = new StringBuilder();
            using (var output = await client.Exec.StartContainerExecAsync(exec.ID, new ContainerExecStartParameters(), ct))
            {
                var buffer = new byte[81920];
                while (true)
                {
                    var read = await output.ReadOutputAsync(buffer, 0, buffer.Length, ct);
                    if (read.EOF)
                    {
                        break;
                    }

                    if (read.Target == MultiplexedStream.TargetStream.StandardOut)
                    {
                        await input.WriteAsync(buffer, 0, read.Count, ct);
                        bytes += read.Count;
                    }
                    else if (stderr.Length < MaxStderr)
                    {
                        stderr.Append(Encoding.UTF8.GetString(buffer, 0, read.Count));
                    }
                }
            }

            input.CloseWrite();
            await client.Containers.WaitContainerAsync(sink.ID, ct);

            var inspect = await client.Exec.InspectContainerExecAsync(exec.ID, ct);
            if (inspect.ExitCode is not 0)
            {
                var message = stderr.ToString().Trim();
                return new DumpResult(dump, bytes, message.Length > 0 ? Truncate(message) : $"the dump command exited with code {inspect.ExitCode}");
            }

            return bytes == 0 ? new DumpResult(dump, 0, "the dump was empty") : new DumpResult(dump, bytes, null);
        }
        catch (DockerApiException ex)
        {
            return new DumpResult(dump, 0, ex.Message);
        }
        finally
        {
            try
            {
                await client.Containers.RemoveContainerAsync(sink.ID, new ContainerRemoveParameters { Force = true }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Removing dump sink failed");
            }
        }
    }

    private static string Truncate(string text) => text.Length <= 500 ? text : text[..500] + "…";
}
