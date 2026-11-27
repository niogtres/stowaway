using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;

namespace Stowaway.Docker.Backups;

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

        if (runner.OnHost)
        {
            return await StartOnHostAsync(ct);
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
            Labels = new Dictionary<string, string> { [ResticRunner.RunnerLabel] = "true", ["stowaway.purpose"] = "dropbox-auth" },
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

        return await WaitForUrlAsync(session, ct);
    }

    /// <summary>Windows-container engines: rclone.exe runs on the host and listens on 127.0.0.1:53682 itself.</summary>
    private async Task<DropboxAuthStatus> StartOnHostAsync(CancellationToken ct)
    {
        var rclone = Path.Combine(runner.ToolsDirectory, "rclone.exe");
        if (!File.Exists(rclone))
        {
            throw new ValidationException($"rclone.exe is missing from {runner.ToolsDirectory}. Reinstall Stowaway.");
        }

        var start = new System.Diagnostics.ProcessStartInfo(rclone)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var arg in new[] { "authorize", "dropbox", "--auth-no-open-browser" })
        {
            start.ArgumentList.Add(arg);
        }

        var session = new Session(Guid.NewGuid(), containerId: "");
        _sessions[session.Id] = session;
        _ = WatchHostAsync(session, start);
        return await WaitForUrlAsync(session, ct);
    }

    private async Task WatchHostAsync(Session session, System.Diagnostics.ProcessStartInfo start)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(session.Cancel.Token);
        timeout.CancelAfter(SessionTimeout);
        var capture = false;
        try
        {
            await HostResticRunner.RunProcessAsync(start, (line, _) => OnLine(session, line, ref capture), timeout.Token);
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
    }

    private async Task<DropboxAuthStatus> WaitForUrlAsync(Session session, CancellationToken ct)
    {
        // Wait for rclone to print its sign-in URL.
        for (var i = 0; i < 100 && session.AuthUrl is null && session.State == DropboxAuthState.Pending; i++)
        {
            await Task.Delay(200, ct);
        }

        if (session.AuthUrl is null && session.State == DropboxAuthState.Pending)
        {
            throw new ValidationException("rclone didn't start the Dropbox sign-in. Is port 53682 already in use?");
        }

        if (session.AuthUrl is { } local)
        {
            // rclone's link points at its own listener on this computer, which only redirects to Dropbox. Follow that
            // redirect here so a browser on another computer (remote access) can open the sign-in page too.
            session.AuthUrl = await ResolveDropboxUrlAsync(local, ct) ?? local;
        }

        return session.Status;
    }

    private async Task<string?> ResolveDropboxUrlAsync(string localUrl, CancellationToken ct)
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(localUrl, ct);
                if (response.Headers.Location is { IsAbsoluteUri: true } location && IsDropboxAuthorizeUrl(location))
                {
                    return location.AbsoluteUri;
                }

                return null;
            }
            catch (HttpRequestException)
            {
                await Task.Delay(300, ct); // the listener may not be up yet
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                break;
            }
        }

        logger.LogInformation("Couldn't resolve the Dropbox sign-in link; using rclone's local link");
        return null;
    }

    internal static bool IsDropboxAuthorizeUrl(Uri url) =>
        url.Scheme == Uri.UriSchemeHttps && (url.Host == "www.dropbox.com" || url.Host == "dropbox.com") && url.AbsolutePath.StartsWith("/oauth2/authorize", StringComparison.Ordinal);

    /// <summary>
    /// Finishes a sign-in whose browser couldn't reach this computer: the user pastes the address Dropbox sent the
    /// browser to (http://localhost:53682/?state=…&amp;code=…) and it is passed to rclone's listener from here.
    /// Only the OAuth parameters are forwarded, and only to the local listener.
    /// </summary>
    public async Task<DropboxAuthStatus> CompleteAsync(Guid id, string pastedUrl, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(id, out var session))
        {
            throw new KeyNotFoundException();
        }

        if (session.State != DropboxAuthState.Pending)
        {
            return session.Status;
        }

        var query = CallbackQuery(pastedUrl)
            ?? throw new ValidationException("That isn't the address Dropbox sent you to. Copy the whole address from the page that couldn't load; it starts with http://localhost:53682/ and contains code=.");

        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(30) };
        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync($"http://127.0.0.1:{CallbackPort}/?{query}", ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ValidationException($"The sign-in on this computer has ended ({ex.Message}). Start again with Connect Dropbox.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new ValidationException("Dropbox didn't accept that address. It may belong to an older sign-in: start again with Connect Dropbox.");
            }
        }

        for (var i = 0; i < 50 && session.State == DropboxAuthState.Pending; i++)
        {
            await Task.Delay(200, ct);
        }

        return session.Status;
    }

    /// <summary>The OAuth parameters of a pasted callback address, re-encoded, or null if it isn't one.</summary>
    internal static string? CallbackQuery(string pastedUrl)
    {
        if (!Uri.TryCreate(pastedUrl.Trim(), UriKind.Absolute, out var url)
            || url.Scheme != Uri.UriSchemeHttp
            || url.Host is not ("localhost" or "127.0.0.1")
            || url.Port != CallbackPort)
        {
            return null;
        }

        var values = System.Web.HttpUtility.ParseQueryString(url.Query);
        if (string.IsNullOrEmpty(values["state"]) || string.IsNullOrEmpty(values["code"]) && string.IsNullOrEmpty(values["error"]))
        {
            return null;
        }

        return string.Join('&', new[] { "state", "code", "error", "error_description" }
            .Where(k => !string.IsNullOrEmpty(values[k]))
            .Select(k => $"{k}={Uri.EscapeDataString(values[k]!)}"));
    }

    private static void OnLine(Session session, string line, ref bool capture)
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
            await ResticRunner.ReadLinesAsync(logs, (line, _) => OnLine(session, line, ref capture), timeout.Token);

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
