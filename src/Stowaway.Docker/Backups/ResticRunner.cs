using System.Formats.Tar;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using Stowaway.Core.Backups;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Stowaway.Docker.Backups;

public sealed class BackupOptions
{
    public const string SectionName = "Stowaway:Backup";

    /// <summary>Pinned restic image used for the ephemeral runner containers.</summary>
    public string ResticImage { get; set; } = "restic/restic:0.19.1";

    /// <summary>Named volume holding restic's cache between runs (makes repeat backups much faster).</summary>
    public string CacheVolume { get; set; } = Stowaway.Core.Inventory.InventoryBuilder.OwnCacheVolume;

    /// <summary>How long to wait for a container to stop gracefully before Docker kills it.</summary>
    public int StopTimeoutSeconds { get; set; } = 60;

    /// <summary>Windows-container engines: where restic.exe and rclone.exe are (shipped in the install folder).</summary>
    public string ToolsDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "tools");

    /// <summary>Windows-container engines: working folder for host-side restic (cache, staging, secrets). Set by the service.</summary>
    public string? HostWorkDirectory { get; set; }
}

/// <param name="Args">restic arguments.</param>
/// <param name="Environment">Variables for restic. Written to a file inside the container (sourced by the
/// entrypoint) so secrets never show up in the container's configuration.</param>
/// <param name="Files">Files to place in the container before it starts, keyed by absolute path.</param>
/// <param name="Network">False runs the container without any network (local and SMB destinations).</param>
/// <param name="Script">Runs this shell script instead of restic (e.g. ssh-keyscan).</param>
public sealed record RunnerRequest(
    IReadOnlyList<string> Args,
    IReadOnlyList<RunnerMount> Mounts,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyDictionary<string, byte[]> Files,
    bool Network = false,
    string Purpose = "restic",
    string? Script = null);

public sealed record RunnerResult(long ExitCode, IReadOnlyList<string> StdOut, IReadOnlyList<string> StdErr)
{
    public string StdOutText => string.Join('\n', StdOut);
}

/// <summary>Runs restic in short-lived, labelled containers and streams their output line by line.</summary>
public sealed class ResticRunner(DockerEngineMonitor engine, IOptions<BackupOptions> options, ILogger<ResticRunner> logger)
{
    public const string RunnerLabel = Stowaway.Core.Inventory.InventoryBuilder.OwnLabel;
    public const string RunnerHostPrefix = "stowaway-";
    public const string EnvFile = CloudRepositories.SecretsDir + "/env";

    // Sources the env file (secrets) and hands over to restic with the remaining arguments.
    private const string ResticWrapper = "set -a; [ -f " + EnvFile + " ] && . " + EnvFile + "; set +a; exec restic \"$@\"";

    private readonly BackupOptions _options = options.Value;
    private readonly SemaphoreSlim _pullLock = new(1, 1);
    private string? _image;
    private HostResticRunner? _host;

    /// <summary>
    /// True when the engine runs Windows containers: restic then runs on the host (Linux runner images can't run
    /// there), and snapshots record host paths.
    /// </summary>
    public bool OnHost => Stowaway.Core.Inventory.Platforms.Of(engine.Current.OsType) == Stowaway.Core.Inventory.Platforms.Windows;

    internal HostResticRunner Host => _host ??= new HostResticRunner(this, _options, logger);

    /// <summary>Clears the host staging folder (metadata and dumps of the run in progress). No-op for Linux engines.</summary>
    public void ClearHostStage()
    {
        if (OnHost)
        {
            Host.ClearStage();
        }
    }

    /// <summary>Where restic.exe and rclone.exe are (Windows engines).</summary>
    public string ToolsDirectory => _options.ToolsDirectory;

    /// <summary>Where a run's staged files go on the host (Windows engines), e.g. SQL Server dumps.</summary>
    public string HostStageDirectory => Host.StageDirectory;

    /// <summary>
    /// The runner image: the pinned restic image plus rclone (Dropbox) and socat (Dropbox sign-in), built
    /// locally on first use. Falls back to the plain restic image if the build fails (e.g. offline).
    /// </summary>
    public string Image => _image ?? _options.ResticImage;

    public string RunnerDockerfile => $"FROM {_options.ResticImage}\nRUN apk add --no-cache rclone socat\nLABEL {RunnerLabel}.image=true\n";

    public string RunnerImageTag =>
        "stowaway/runner:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(RunnerDockerfile)))[..12];

    /// <summary>True when the extended runner image (with rclone) is available.</summary>
    public bool HasRclone => _image == RunnerImageTag;

    public DockerClient CreateClient(TimeSpan? timeout = null) =>
        new DockerClientBuilder()
            .WithEndpoint(engine.ActiveEndpoint ?? throw new InvalidOperationException("The Docker engine is not available."))
            .WithTimeout(timeout ?? Timeout.InfiniteTimeSpan)
            .Build();

    public async Task<RunnerResult> RunAsync(RunnerRequest request, Action<string, bool>? onLine, CancellationToken ct)
    {
        if (OnHost)
        {
            return await Host.RunAsync(request, onLine, ct);
        }

        using var client = CreateClient();
        await EnsureImageAsync(client, ct);

        var mounts = request.Mounts
            .Select(m => new Mount { Type = m.Type, Source = m.Source, Target = m.Target, ReadOnly = m.ReadOnly })
            .Append(new Mount { Type = "volume", Source = _options.CacheVolume, Target = "/cache" })
            .ToList();

        var created = await client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = Image,
            // Recognisable hostname: restic records it in repository locks, which lets stale locks of ours be told apart.
            Hostname = RunnerHostPrefix + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)),
            Entrypoint = ["sh"],
            Cmd = request.Script is { } script ? ["-c", script] : ["-c", ResticWrapper, "restic", .. request.Args],
            Env = ["RESTIC_CACHE_DIR=/cache"],
            Labels = new Dictionary<string, string> { [RunnerLabel] = "true", ["stowaway.purpose"] = request.Purpose },
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
            var files = new Dictionary<string, byte[]>(request.Files);
            if (request.Environment.Count > 0)
            {
                files[EnvFile] = Encoding.UTF8.GetBytes(EnvFileContent(request.Environment));
            }

            if (files.Count > 0)
            {
                using var tar = BuildTar(files);
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

    public async Task EnsureImageAsync(DockerClient client, CancellationToken ct)
    {
        if (_image is not null || OnHost)
        {
            return; // Windows engines don't use the Linux runner image
        }

        await _pullLock.WaitAsync(ct);
        try
        {
            if (_image is not null)
            {
                return;
            }

            if (await ExistsAsync(client, RunnerImageTag, ct))
            {
                _image = RunnerImageTag;
                return;
            }

            if (!await ExistsAsync(client, _options.ResticImage, ct))
            {
                var (repository, tag) = SplitImage(_options.ResticImage);
                logger.LogInformation("Pulling {Image}", _options.ResticImage);
                await client.Images.CreateImageAsync(new ImagesCreateParameters { FromImage = repository, Tag = tag }, null, new Progress<JSONMessage>(), ct);
            }

            try
            {
                logger.LogInformation("Building runner image {Image}", RunnerImageTag);
                using var context = BuildTar(new Dictionary<string, byte[]> { ["Dockerfile"] = Encoding.UTF8.GetBytes(RunnerDockerfile) });
                await client.Images.BuildImageFromDockerfileAsync(
                    new ImageBuildParameters { Tags = [RunnerImageTag], Remove = true, ForceRemove = true },
                    context, null, null, new Progress<JSONMessage>(m =>
                    {
                        if (m.Error?.Message is { Length: > 0 } error)
                        {
                            logger.LogWarning("Runner image build: {Error}", error);
                        }
                    }), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Building the runner image failed");
            }

            if (await ExistsAsync(client, RunnerImageTag, ct))
            {
                _image = RunnerImageTag;
            }
            else
            {
                logger.LogWarning("Using {Image} without rclone: Dropbox destinations won't work until the runner image can be built", _options.ResticImage);
                _image = _options.ResticImage;
            }
        }
        finally
        {
            _pullLock.Release();
        }
    }

    private static async Task<bool> ExistsAsync(DockerClient client, string image, CancellationToken ct)
    {
        try
        {
            await client.Images.InspectImageAsync(image, ct);
            return true;
        }
        catch (DockerImageNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Shell-safe <c>KEY='value'</c> lines for the entrypoint to source.</summary>
    internal static string EnvFileContent(IReadOnlyDictionary<string, string> environment)
    {
        var sb = new StringBuilder();
        foreach (var (key, value) in environment)
        {
            if (!key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') || key.Length == 0 || char.IsAsciiDigit(key[0]))
            {
                throw new ArgumentException($"Invalid environment variable name '{key}'.");
            }

            sb.Append(key).Append("='").Append(value.Replace("'", "'\\''")).Append("'\n");
        }

        return sb.ToString();
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

    internal static async Task ReadLinesAsync(MultiplexedStream stream, Action<string, bool> onLine, CancellationToken ct)
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
