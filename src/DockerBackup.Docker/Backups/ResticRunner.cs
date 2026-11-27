using System.Formats.Tar;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using DockerBackup.Core.Backups;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DockerBackup.Docker.Backups;

public sealed class BackupOptions
{
    public const string SectionName = "DockerBackup:Backup";

    /// <summary>Pinned restic image used for the ephemeral runner containers.</summary>
    public string ResticImage { get; set; } = "restic/restic:0.19.1";

    /// <summary>Named volume holding restic's cache between runs (makes repeat backups much faster).</summary>
    public string CacheVolume { get; set; } = "dockerbackup-restic-cache";

    /// <summary>How long to wait for a container to stop gracefully before Docker kills it.</summary>
    public int StopTimeoutSeconds { get; set; } = 60;
}

/// <param name="Args">restic arguments (the image's entrypoint is restic).</param>
/// <param name="Files">Files to place in the container before it starts, keyed by absolute path.</param>
/// <param name="Network">False runs the container without any network (local and SMB destinations).</param>
public sealed record RunnerRequest(
    IReadOnlyList<string> Args,
    IReadOnlyList<RunnerMount> Mounts,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyDictionary<string, byte[]> Files,
    bool Network = false,
    string Purpose = "restic");

public sealed record RunnerResult(long ExitCode, IReadOnlyList<string> StdOut, IReadOnlyList<string> StdErr)
{
    public string StdOutText => string.Join('\n', StdOut);
}

/// <summary>Runs restic in short-lived, labelled containers and streams their output line by line.</summary>
public sealed class ResticRunner(DockerEngineMonitor engine, IOptions<BackupOptions> options, ILogger<ResticRunner> logger)
{
    public const string RunnerLabel = "dockerbackup.runner";
    private readonly BackupOptions _options = options.Value;
    private readonly SemaphoreSlim _pullLock = new(1, 1);

    public string Image => _options.ResticImage;

    public DockerClient CreateClient(TimeSpan? timeout = null) =>
        new DockerClientBuilder()
            .WithEndpoint(engine.ActiveEndpoint ?? throw new InvalidOperationException("The Docker engine is not available."))
            .WithTimeout(timeout ?? Timeout.InfiniteTimeSpan)
            .Build();

    public async Task<RunnerResult> RunAsync(RunnerRequest request, Action<string, bool>? onLine, CancellationToken ct)
    {
        using var client = CreateClient();
        await EnsureImageAsync(client, ct);

        var mounts = request.Mounts
            .Select(m => new Mount { Type = m.Type, Source = m.Source, Target = m.Target, ReadOnly = m.ReadOnly })
            .Append(new Mount { Type = "volume", Source = _options.CacheVolume, Target = "/cache" })
            .ToList();

        var created = await client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = Image,
            Cmd = request.Args.ToList(),
            Env = request.Environment.Select(kv => $"{kv.Key}={kv.Value}").Append("RESTIC_CACHE_DIR=/cache").ToList(),
            Labels = new Dictionary<string, string> { [RunnerLabel] = "true", ["dockerbackup.purpose"] = request.Purpose },
            HostConfig = new HostConfig
            {
                Mounts = mounts,
                NetworkMode = request.Network ? "bridge" : "none",
                SecurityOpt = ["no-new-privileges"],
            },
        }, ct);

        var id = created.ID;
        try
        {
            if (request.Files.Count > 0)
            {
                using var tar = BuildTar(request.Files);
                await client.Containers.ExtractArchiveToContainerAsync(id, new CopyToContainerParameters { Path = "/" }, tar, ct);
            }

            await client.Containers.StartContainerAsync(id, new ContainerStartParameters(), ct);

            var stdout = new List<string>();
            var stderr = new List<string>();
            using (var logs = await client.Containers.GetContainerLogsAsync(
                       id, new ContainerLogsParameters { Follow = true, ShowStdout = true, ShowStderr = true }, ct))
            {
                await ReadLinesAsync(logs, (line, isErr) =>
                {
                    (isErr ? stderr : stdout).Add(line);
                    onLine?.Invoke(line, isErr);
                }, ct);
            }

            var wait = await client.Containers.WaitContainerAsync(id, ct);
            return new RunnerResult(wait.StatusCode, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            await TryAsync(() => client.Containers.KillContainerAsync(id, new ContainerKillParameters(), CancellationToken.None));
            throw;
        }
        finally
        {
            await TryAsync(() => client.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters { Force = true }, CancellationToken.None));
        }
    }

    /// <summary>Removes runner containers left behind by a crash or a service restart.</summary>
    public async Task RemoveLeftoverRunnersAsync(CancellationToken ct)
    {
        using var client = CreateClient(TimeSpan.FromSeconds(30));
        var leftovers = await client.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>> { ["label"] = new Dictionary<string, bool> { [$"{RunnerLabel}=true"] = true } },
        }, ct);
        foreach (var c in leftovers)
        {
            logger.LogWarning("Removing leftover runner container {Id}", c.ID[..12]);
            await TryAsync(() => client.Containers.RemoveContainerAsync(c.ID, new ContainerRemoveParameters { Force = true }, ct));
        }
    }

    private async Task EnsureImageAsync(DockerClient client, CancellationToken ct)
    {
        await _pullLock.WaitAsync(ct);
        try
        {
            try
            {
                await client.Images.InspectImageAsync(Image, ct);
                return;
            }
            catch (DockerImageNotFoundException)
            {
            }

            var (repository, tag) = SplitImage(Image);
            logger.LogInformation("Pulling {Image}", Image);
            await client.Images.CreateImageAsync(new ImagesCreateParameters { FromImage = repository, Tag = tag }, null, new Progress<JSONMessage>(), ct);
        }
        finally
        {
            _pullLock.Release();
        }
    }

    internal static (string Repository, string Tag) SplitImage(string image)
    {
        var colon = image.LastIndexOf(':');
        return colon > image.LastIndexOf('/') ? (image[..colon], image[(colon + 1)..]) : (image, "latest");
    }

    private static MemoryStream BuildTar(IReadOnlyDictionary<string, byte[]> files)
    {
        var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true))
        {
            var directories = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var path in files.Keys)
            {
                var parts = path.Trim('/').Split('/');
                for (var i = 1; i < parts.Length; i++)
                {
                    directories.Add(string.Join('/', parts[..i]) + "/");
                }
            }

            foreach (var dir in directories)
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, dir) { Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute });
            }

            foreach (var (path, content) in files)
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path.TrimStart('/'))
                {
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    DataStream = new MemoryStream(content),
                });
            }
        }

        stream.Position = 0;
        return stream;
    }

    private static async Task ReadLinesAsync(MultiplexedStream stream, Action<string, bool> onLine, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var pending = new Dictionary<bool, StringBuilder> { [false] = new(), [true] = new() };
        var decoders = new Dictionary<bool, Decoder> { [false] = Encoding.UTF8.GetDecoder(), [true] = Encoding.UTF8.GetDecoder() };
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];

        while (true)
        {
            var result = await stream.ReadOutputAsync(buffer, 0, buffer.Length, ct);
            if (result.EOF)
            {
                break;
            }

            var isErr = result.Target == MultiplexedStream.TargetStream.StandardError;
            var count = decoders[isErr].GetChars(buffer, 0, result.Count, chars, 0);
            var sb = pending[isErr];
            for (var i = 0; i < count; i++)
            {
                var ch = chars[i];
                if (ch == '\n')
                {
                    Emit(sb, isErr);
                }
                else if (ch != '\r')
                {
                    sb.Append(ch);
                }
            }
        }

        Emit(pending[false], false);
        Emit(pending[true], true);

        void Emit(StringBuilder sb, bool isErr)
        {
            if (sb.Length > 0)
            {
                onLine(sb.ToString(), isErr);
                sb.Clear();
            }
        }
    }

    private async Task TryAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Runner cleanup step failed");
        }
    }
}
