using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;

namespace Stowaway.Docker.Backups;

public enum SignInState
{
    Pending,
    Ready,
    Failed,
}

public sealed record SignInStatus(Guid Id, SignInState State, string? AuthUrl, string? Error);

/// <summary>A cloud service rclone signs in to in the browser.</summary>
/// <param name="Name">rclone's backend name, also the destination descriptor's <c>SignIn</c> value and the API route.</param>
/// <param name="IsAuthorizeUrl">Whether a redirect from rclone's local listener points at the service's own sign-in page.</param>
public sealed record SignInBackend(string Name, string Label, Func<Uri, bool> IsAuthorizeUrl);

/// <summary>
/// Signs in to a cloud service (Dropbox, OneDrive) with rclone's own OAuth flow. rclone runs in a runner container and listens on its
/// loopback port 53682; socat bridges that to the host's 127.0.0.1:53682, where the service redirects the browser.
/// The resulting token stays server-side and is handed to destination creation by session id.
/// </summary>
public sealed partial class RcloneSignInService(ResticRunner runner, ILogger<RcloneSignInService> logger)
{
    public const int CallbackPort = 53682;
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();

    public static readonly SignInBackend Dropbox = new("dropbox", "Dropbox", IsDropboxAuthorizeUrl);
    public static readonly SignInBackend OneDrive = new("onedrive", "OneDrive", IsMicrosoftAuthorizeUrl);

    /// <summary>The services Stowaway signs in to, by rclone backend name.</summary>
    public static IReadOnlyDictionary<string, SignInBackend> Backends { get; } =
        new Dictionary<string, SignInBackend>(StringComparer.Ordinal) { [Dropbox.Name] = Dropbox, [OneDrive.Name] = OneDrive };

    public async Task<SignInStatus> StartAsync(string backendName, CancellationToken ct)
    {
        var backend = Backends.GetValueOrDefault(backendName) ?? throw new ValidationException($"Stowaway doesn't sign in to {backendName}.");
        foreach (var old in _sessions.Values.Where(s => s.State == SignInState.Pending))
        {
            await old.Cancel.CancelAsync(); // only one sign-in can own the callback port
        }

        if (runner.OnHost)
        {
            return await StartOnHostAsync(backend, ct);
        }

        using var client = runner.CreateClient(TimeSpan.FromMinutes(5));
        await runner.EnsureImageAsync(client, ct);
        if (!runner.HasRclone)
        {
            throw new ValidationException($"{backend.Label} needs the runner image with rclone, which couldn't be built. Check the service log.");
        }

        var created = await client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = runner.Image,
            Entrypoint = ["sh"],
            Cmd = ["-c", $"socat TCP-LISTEN:{CallbackPort + 1},fork,reuseaddr TCP:127.0.0.1:{CallbackPort} & exec rclone authorize {backend.Name} --auth-no-open-browser"],
            ExposedPorts = new Dictionary<string, EmptyStruct> { [$"{CallbackPort + 1}/tcp"] = default },
            Labels = new Dictionary<string, string> { [ResticRunner.RunnerLabel] = "true", ["stowaway.purpose"] = $"{backend.Name}-auth" },
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

        var session = new Session(Guid.NewGuid(), created.ID, backend);
        _sessions[session.Id] = session;
        await client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct);
        _ = WatchAsync(session);

        return await WaitForUrlAsync(session, ct);
    }

    /// <summary>Windows-container engines: rclone.exe runs on the host and listens on 127.0.0.1:53682 itself.</summary>
    private async Task<SignInStatus> StartOnHostAsync(SignInBackend backend, CancellationToken ct)
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
        foreach (var arg in new[] { "authorize", backend.Name, "--auth-no-open-browser" })
        {
            start.ArgumentList.Add(arg);
        }

        var session = new Session(Guid.NewGuid(), containerId: "", backend);
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
            if (session.State != SignInState.Ready)
            {
                session.Fail($"{session.Backend.Label} sign-in ended without a token.");
            }
        }
        catch (OperationCanceledException)
        {
            session.Fail(session.Cancel.IsCancellationRequested ? "Replaced by a newer sign-in." : "The sign-in timed out.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Service} sign-in failed", session.Backend.Label);
            session.Fail(ex.Message);
        }
    }

    private async Task<SignInStatus> WaitForUrlAsync(Session session, CancellationToken ct)
    {
        // Wait for rclone to print its sign-in URL.
        for (var i = 0; i < 100 && session.AuthUrl is null && session.State == SignInState.Pending; i++)
        {
            await Task.Delay(200, ct);
        }

        if (session.AuthUrl is null && session.State == SignInState.Pending)
        {
            throw new ValidationException($"rclone didn't start the {session.Backend.Label} sign-in. Is port 53682 already in use?");
        }

        if (session.AuthUrl is { } local)
        {
            // rclone's link points at its own listener on this computer, which only redirects to the service. Follow that
            // redirect here so a browser on another computer (remote access) can open the sign-in page too.
            session.AuthUrl = await ResolveAuthorizeUrlAsync(local, session.Backend, ct) ?? local;
        }

        return session.Status;
    }

    private async Task<string?> ResolveAuthorizeUrlAsync(string localUrl, SignInBackend backend, CancellationToken ct)
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(localUrl, ct);
                if (response.Headers.Location is { IsAbsoluteUri: true } location && backend.IsAuthorizeUrl(location))
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

        logger.LogInformation("Couldn't resolve the {Service} sign-in link; using rclone's local link", backend.Label);
        return null;
    }

    internal static bool IsDropboxAuthorizeUrl(Uri url) =>
        url.Scheme == Uri.UriSchemeHttps && (url.Host == "www.dropbox.com" || url.Host == "dropbox.com") && url.AbsolutePath.StartsWith("/oauth2/authorize", StringComparison.Ordinal);

    /// <summary>Microsoft's sign-in page (work, school and personal accounts), e.g. /common/oauth2/v2.0/authorize.</summary>
    internal static bool IsMicrosoftAuthorizeUrl(Uri url) =>
        url.Scheme == Uri.UriSchemeHttps && url.Host == "login.microsoftonline.com" && url.AbsolutePath.EndsWith("/oauth2/v2.0/authorize", StringComparison.Ordinal);

    /// <summary>
    /// Finishes a sign-in whose browser couldn't reach this computer: the user pastes the address the service sent the
    /// browser to (http://localhost:53682/?state=…&amp;code=…) and it is passed to rclone's listener from here.
    /// Only the OAuth parameters are forwarded, and only to the local listener.
    /// </summary>
    public async Task<SignInStatus> CompleteAsync(Guid id, string pastedUrl, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(id, out var session))
        {
            throw new KeyNotFoundException();
        }

        if (session.State != SignInState.Pending)
        {
            return session.Status;
        }

        var query = CallbackQuery(pastedUrl)
            ?? throw new ValidationException($"That isn't the address {session.Backend.Label} sent you to. Copy the whole address from the page that couldn't load; it starts with http://localhost:53682/ and contains code=.");

        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(30) };
        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync($"http://127.0.0.1:{CallbackPort}/?{query}", ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ValidationException($"The sign-in on this computer has ended ({ex.Message}). Start again with Connect {session.Backend.Label}.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new ValidationException($"{session.Backend.Label} didn't accept that address. It may belong to an older sign-in: start again with Connect {session.Backend.Label}.");
            }
        }

        for (var i = 0; i < 50 && session.State == SignInState.Pending; i++)
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
            session.State = SignInState.Ready;
            capture = false;
        }
    }

    public SignInStatus? Get(Guid id) => _sessions.TryGetValue(id, out var s) ? s.Status : null;

    /// <summary>Returns the token once and forgets the session. A pending session, or one for another service, is left alone.</summary>
    public string? TakeToken(Guid id, SignInBackend backend) =>
        _sessions.TryGetValue(id, out var s) && s.Backend == backend && s.State == SignInState.Ready && _sessions.TryRemove(id, out _) ? s.Token : null;

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

            if (session.State != SignInState.Ready)
            {
                session.Fail($"{session.Backend.Label} sign-in ended without a token.");
            }
        }
        catch (OperationCanceledException)
        {
            session.Fail(session.Cancel.IsCancellationRequested ? "Replaced by a newer sign-in." : "The sign-in timed out.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Service} sign-in failed", session.Backend.Label);
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
                logger.LogDebug(ex, "Removing the sign-in container failed");
            }
        }
    }

    [GeneratedRegex(@"http://127\.0\.0\.1:53682/auth\?state=[A-Za-z0-9_\-]+")]
    private static partial Regex AuthUrl();

    private sealed class Session(Guid id, string containerId, SignInBackend backend)
    {
        public Guid Id { get; } = id;
        public SignInBackend Backend { get; } = backend;
        public string ContainerId { get; } = containerId;
        public CancellationTokenSource Cancel { get; } = new();
        public volatile string? AuthUrl;
        public volatile string? Token;
        public volatile string? Error;
        private volatile int _state;

        public SignInState State
        {
            get => (SignInState)_state;
            set => _state = (int)value;
        }

        public SignInStatus Status => new(Id, State, AuthUrl, Error);

        public void Fail(string error)
        {
            if (State == SignInState.Pending)
            {
                Error = error;
                State = SignInState.Failed;
            }
        }
    }
}
