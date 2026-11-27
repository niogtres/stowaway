using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using Stowaway.Core.Backups;
using Microsoft.Extensions.Logging;

namespace Stowaway.Docker.Backups;

public sealed record DumpResult(DumpCommand Dump, long Bytes, string? Error);

/// <summary>
/// Streams database dumps from inside the database containers into a temporary volume, which the backup then
/// includes. The dump runs with the database's own tools; a small "sink" runner writes the stream to disk.
/// </summary>
public sealed class DumpService(ResticRunner runner, ILogger<DumpService> logger, IDatabaseCredentialSource? credentials = null)
{
    private const int MaxStderr = 4096;

    public static string VolumeName(Guid runId) => $"stowaway-dumps-{runId:N}";

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
            Labels = new Dictionary<string, string> { [ResticRunner.RunnerLabel] = "true", ["stowaway.purpose"] = "dump" },
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

            // Saved credentials travel as exec environment: never part of the container's configuration.
            var environment = credentials is null ? [] : await credentials.EnvironmentForAsync(dump.Container.Name, ct);
            var exec = await client.Exec.CreateContainerExecAsync(dump.Container.Id, new ContainerExecCreateParameters
            {
                AttachStdout = true,
                AttachStderr = true,
                Cmd = DockerExec.WithExitCode(dump.Command),
                Env = environment.Count > 0 ? environment.ToList() : null,
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
                    else
                    {
                        stderr.Append(Encoding.UTF8.GetString(buffer, 0, read.Count));
                        if (stderr.Length > MaxStderr * 2)
                        {
                            stderr.Remove(MaxStderr, stderr.Length - MaxStderr - 256); // keep the start and the tail (exit code)
                        }
                    }
                }
            }

            var exitCode = await DockerExec.ExitCodeAsync(client, exec.ID, stderr, ct);
            if (exitCode is not 0)
            {
                var message = stderr.ToString().Trim();
                return new DumpResult(dump, bytes, message.Length > 0 ? Truncate(message) : $"the dump command exited with code {exitCode}");
            }

            if (bytes == 0)
            {
                return new DumpResult(dump, 0, "the dump was empty");
            }

            // Normally closing stdin ends the sink. The end-of-input signal doesn't always make it through Docker's
            // attach connection, so don't depend on it: the sink is done once it has written every byte it was sent.
            input.CloseWrite();
            return await SinkFinishedAsync(client, sink.ID, dump.FileName, bytes, ct)
                ? new DumpResult(dump, bytes, null)
                : new DumpResult(dump, bytes, "the dump couldn't be written completely");
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

    /// <summary>
    /// Waits for the sink to exit after its input was closed or, failing that, until its file holds all
    /// <paramref name="bytes"/> (cat writes as it reads, so a complete file means it's done).
    /// </summary>
    private static async Task<bool> SinkFinishedAsync(DockerClient client, string sinkId, string fileName, long bytes, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = (await client.Containers.InspectContainerAsync(sinkId, ct)).State;
            if (state is not { Running: true })
            {
                return state?.ExitCode == 0;
            }

            try
            {
                var size = await DockerExec.RunAsync(client, sinkId, ["stat", "-c", "%s", $"/dumps/{fileName}"], ct, 64);
                if (size.ExitCode == 0 && long.TryParse(size.StdOut.Trim(), out var written) && written == bytes)
                {
                    return true; // complete; the sink is removed (and so stopped) by the caller
                }
            }
            catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                continue; // it exited between the two checks: the next look at its state says how
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }

        return false;
    }

    private static string Truncate(string text) => text.Length <= 500 ? text : text[..500] + "…";
}
