using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace Stowaway.Docker;

public sealed record ExecResult(long ExitCode, string StdOut, string StdErr);

/// <summary>Runs a command inside a running container and collects its (bounded) output.</summary>
public static class DockerExec
{
    private const string ExitMarker = "__stowaway_exit=";

    /// <summary>
    /// Wraps a command so it prints its own exit code on stderr. Docker may delete a finished exec before it can be
    /// inspected ("No such exec instance"), so asking Docker for the exit code afterwards isn't reliable.
    /// Needs <c>sh</c> in the container, which every image Stowaway execs into has.
    /// </summary>
    public static List<string> WithExitCode(IEnumerable<string> command) =>
        ["sh", "-c", $"\"$@\"; echo \"{ExitMarker}$?\" >&2", "sh", .. command];

    /// <summary>Takes the exit code the wrapped command printed out of its stderr, or null if it isn't there.</summary>
    public static long? TakeExitCode(StringBuilder stderr)
    {
        var text = stderr.ToString();
        var at = text.LastIndexOf(ExitMarker, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var end = text.IndexOf('\n', at);
        var value = (end < 0 ? text[(at + ExitMarker.Length)..] : text[(at + ExitMarker.Length)..end]).Trim();
        stderr.Remove(at, (end < 0 ? text.Length : end + 1) - at);
        return long.TryParse(value, out var code) ? code : null;
    }

    /// <summary>The exit code from the marker, else from Docker (if the exec still exists), else -1.</summary>
    public static async Task<long> ExitCodeAsync(DockerClient client, string execId, StringBuilder stderr, CancellationToken ct)
    {
        if (TakeExitCode(stderr) is { } code)
        {
            return code;
        }

        try
        {
            return (await client.Exec.InspectContainerExecAsync(execId, ct)).ExitCode ?? -1;
        }
        catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return -1;
        }
    }

    /// <param name="windows">
    /// A Windows container: no <c>sh</c> to wrap the command in, so the exit code comes from Docker. Windows has no
    /// argv either: Docker joins the arguments into one command line, so call executables directly with plain
    /// arguments (never <c>cmd /c</c> with anything it would re-parse).
    /// </param>
    public static async Task<ExecResult> RunAsync(DockerClient client, string containerId, IList<string> command, CancellationToken ct, int maxOutput = 64 * 1024, IList<string>? environment = null, bool windows = false)
    {
        var exec = await client.Exec.CreateContainerExecAsync(containerId, new ContainerExecCreateParameters
        {
            Cmd = windows ? command : WithExitCode(command),
            Env = environment,
            AttachStdout = true,
            AttachStderr = true,
        }, ct);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using (var stream = await client.Exec.StartContainerExecAsync(exec.ID, new ContainerExecStartParameters(), ct))
        {
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var read = await stream.ReadOutputAsync(buffer, 0, buffer.Length, ct);
                if (read.EOF)
                {
                    break;
                }

                var target = read.Target == MultiplexedStream.TargetStream.StandardError ? stderr : stdout;
                if (target.Length < maxOutput || target == stderr)
                {
                    target.Append(Encoding.UTF8.GetString(buffer, 0, read.Count));
                }

                if (stderr.Length > maxOutput * 2)
                {
                    stderr.Remove(0, stderr.Length - maxOutput); // keep the tail: the exit code comes last
                }
            }
        }

        var exitCode = await ExitCodeAsync(client, exec.ID, stderr, ct);
        return new ExecResult(exitCode, stdout.ToString().Trim(), stderr.ToString().Trim());
    }

    /// <summary>
    /// Runs a command in a Windows container and hands its stdout to <paramref name="consume"/> as a stream (e.g. a tar
    /// archive). Returns the exit code and the (bounded) stderr.
    /// </summary>
    public static async Task<(long ExitCode, string StdErr)> StreamAsync(DockerClient client, string containerId, IList<string> command, Func<Stream, Task> consume, CancellationToken ct)
    {
        var exec = await client.Exec.CreateContainerExecAsync(containerId, new ContainerExecCreateParameters
        {
            Cmd = command,
            AttachStdout = true,
            AttachStderr = true,
        }, ct);

        var pipe = new System.IO.Pipelines.Pipe();
        var stderr = new StringBuilder();
        using var output = await client.Exec.StartContainerExecAsync(exec.ID, new ContainerExecStartParameters(), ct);
        var produce = Task.Run(async () =>
        {
            Exception? failure = null;
            try
            {
                var buffer = new byte[81920];
                while (true)
                {
                    var read = await output.ReadOutputAsync(buffer, 0, buffer.Length, ct);
                    if (read.EOF)
                    {
                        break;
                    }

                    if (read.Target == MultiplexedStream.TargetStream.StandardError)
                    {
                        if (stderr.Length < 16 * 1024)
                        {
                            stderr.Append(Encoding.UTF8.GetString(buffer, 0, read.Count));
                        }
                    }
                    else if ((await pipe.Writer.WriteAsync(buffer.AsMemory(0, read.Count), ct)).IsCompleted)
                    {
                        break; // the reader gave up
                    }
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                await pipe.Writer.CompleteAsync(failure);
            }
        }, ct);

        try
        {
            await using var stream = pipe.Reader.AsStream();
            await consume(stream);
            await stream.CopyToAsync(Stream.Null, ct); // drain anything after the end of the archive
        }
        finally
        {
            await pipe.Reader.CompleteAsync();
            await produce;
        }

        return (await ExitCodeAsync(client, exec.ID, stderr, ct), stderr.ToString().Trim());
    }
}
