using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace DockerBackup.Docker;

public sealed record ExecResult(long ExitCode, string StdOut, string StdErr);

/// <summary>Runs a command inside a running container and collects its (bounded) output.</summary>
public static class DockerExec
{
    public static async Task<ExecResult> RunAsync(DockerClient client, string containerId, IList<string> command, CancellationToken ct, int maxOutput = 64 * 1024)
    {
        var exec = await client.Exec.CreateContainerExecAsync(containerId, new ContainerExecCreateParameters
        {
            Cmd = command,
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
                if (target.Length < maxOutput)
                {
                    target.Append(Encoding.UTF8.GetString(buffer, 0, read.Count));
                }
            }
        }

        var inspect = await client.Exec.InspectContainerExecAsync(exec.ID, ct);
        return new ExecResult(inspect.ExitCode ?? -1, stdout.ToString().Trim(), stderr.ToString().Trim());
    }
}
