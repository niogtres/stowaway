using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;

namespace DockerBackup.Docker.Backups;

public enum DropboxAuthState
{
    Pending,
    Ready,
    Failed,
}

public sealed record DropboxAuthStatus(Guid Id, DropboxAuthState State, string? AuthUrl, string? Error);

/// <summary>
/// Signs in to Dropbox with rclone's own OAuth flow. rclone runs in a runner container and listens on its
/// loopback port 53682; socat bridges that to the host's 127.0.0.1:53682, where Dropbox redirects the browser.
/// The resulting token stays server-side and is handed to destination creation by session id.
/// </summary>
public sealed partial class DropboxAuthService(ResticRunner runner, ILogger<DropboxAuthService> logger)
{
    public const int CallbackPort = 53682;
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();

    public async Task<DropboxAuthStatus> StartAsync(CancellationToken ct)
    {
        foreach (var old in _sessions.Values.Where(s => s.State == DropboxAuthState.Pending))
        {
            await old.Cancel.CancelAsync(); // only one sign-in can own the callback port
        }

        using var client = runner.CreateClient(TimeSpan.FromMinutes(5));
        await runner.EnsureImageAsync(client, ct);
        if (!runner.HasRclone)
        {
            throw new ValidationException("Dropbox needs the runner image with rclone, which couldn't be built. Check the service log.");
        }

        var created = await client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = runner.Image,
            Entrypoint = ["sh"],
            Cmd = ["-c", $"socat TCP-LISTEN:{CallbackPort + 1},fork,reuseaddr TCP:127.0.0.1:{CallbackPort} & exec rclone authorize dropbox --auth-no-open-browser"],
            ExposedPorts = new Dictionary<string, EmptyStruct> { [$"{CallbackPort + 1}/tcp"] = default },
            Labels = new Dictionary<string, string> { [ResticRunner.RunnerLabel] = "true", ["dockerbackup.purpose"] = "dropbox-auth" },
            HostConfig = new HostConfig
            {
                NetworkMode = "bridge",
                SecurityOpt = ["no-new-privileges"],
                PortBindings = new Dictionary<string, IList<PortBinding>>
                {
                    [$"{CallbackPort + 1}/tcp"] = [new PortBinding { HostIP = "127.0.0.1", HostPort = CallbackPort.ToString(System.Globalization.CultureInfo.InvariantCulture) }],
                },
            },
        }, ct);

        var session = new Session(Guid.NewGuid(), created.ID);
        _sessions[session.Id] = session;
        await client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct);
        _ = WatchAsync(session);

        // Wait for rclone to print its sign-in URL.
        for (var i = 0; i < 100 && session.AuthUrl is null && session.State == DropboxAuthState.Pending; i++)
        {
            await Task.Delay(200, ct);
        }

        return session.AuthUrl is null && session.State == DropboxAuthState.Pending
            ? throw new ValidationException("rclone didn't start the Dropbox sign-in. Is port 53682 already in use?")
            : session.Status;
    }

    public DropboxAuthStatus? Get(Guid id) => _sessions.TryGetValue(id, out var s) ? s.Status : null;

    /// <summary>Returns the token once and forgets the session. A pending session is left alone.</summary>
    public string? TakeToken(Guid id) =>
        _sessions.TryGetValue(id, out var s) && s.State == DropboxAuthState.Ready && _sessions.TryRemove(id, out _) ? s.Token : null;

    private async Task WatchAsync(Session session)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(session.Cancel.Token);
        timeout.CancelAfter(SessionTimeout);
        using var client = runner.CreateClient();
        try
        {
            var capture = false;
            using var logs = await client.Containers.GetContainerLogsAsync(session.ContainerId,
                new ContainerLogsParameters { Follow = true, ShowStdout = true, ShowStderr = true }, timeout.Token);
            await ResticRunner.ReadLinesAsync(logs, (line, _) =>
            {
                if (session.AuthUrl is null && AuthUrl().Match(line) is { Success: true } url)
                {
                    session.AuthUrl = url.Value;
                }
                else if (line.Contains("Paste the following", StringComparison.Ordinal))
                {
                    capture = true;
                }
                else if (capture && line.TrimStart().StartsWith('{'))
                {
                    session.Token = line.Trim();
                    session.State = DropboxAuthState.Ready;
                    capture = false;
                }
            }, timeout.Token);

            if (session.State != DropboxAuthState.Ready)
            {
                session.Fail("Dropbox sign-in ended without a token.");
            }
        }
        catch (OperationCanceledException)
        {
            session.Fail(session.Cancel.IsCancellationRequested ? "Replaced by a newer sign-in." : "The sign-in timed out.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Dropbox sign-in failed");
            session.Fail(ex.Message);
        }
        finally
        {
            try
            {
                await client.Containers.RemoveContainerAsync(session.ContainerId, new ContainerRemoveParameters { Force = true }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Removing the Dropbox sign-in container failed");
            }
        }
    }

    [GeneratedRegex(@"http://127\.0\.0\.1:53682/auth\?state=[A-Za-z0-9_\-]+")]
    private static partial Regex AuthUrl();

    private sealed class Session(Guid id, string containerId)
    {
        public Guid Id { get; } = id;
        public string ContainerId { get; } = containerId;
        public CancellationTokenSource Cancel { get; } = new();
        public volatile string? AuthUrl;
        public volatile string? Token;
        public volatile string? Error;
        private volatile int _state;

        public DropboxAuthState State
        {
            get => (DropboxAuthState)_state;
            set => _state = (int)value;
        }

        public DropboxAuthStatus Status => new(Id, State, AuthUrl, Error);

        public void Fail(string error)
        {
            if (State == DropboxAuthState.Pending)
            {
                Error = error;
                State = DropboxAuthState.Failed;
            }
        }
    }
}
