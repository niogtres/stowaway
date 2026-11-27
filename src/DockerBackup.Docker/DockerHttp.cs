using System.IO.Pipes;

namespace DockerBackup.Docker;

/// <summary>
/// Plain HTTP access to the Engine API for endpoints the client library doesn't cover (e.g. <c>/system/df</c>).
/// Supports named pipes and plain tcp/http endpoints.
/// </summary>
public static class DockerHttp
{
    public static HttpClient Create(Uri endpoint, TimeSpan timeout)
    {
        switch (endpoint.Scheme)
        {
            case "npipe":
            {
                var server = string.IsNullOrEmpty(endpoint.Host) ? "." : endpoint.Host;
                var path = endpoint.AbsolutePath.TrimStart('/');
                var pipeName = path.StartsWith("pipe/", StringComparison.OrdinalIgnoreCase) ? path["pipe/".Length..] : path;
                var handler = new SocketsHttpHandler
                {
                    ConnectCallback = async (_, ct) =>
                    {
                        var pipe = new NamedPipeClientStream(server, pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                        await pipe.ConnectAsync(ct);
                        return pipe;
                    },
                };
                return new HttpClient(handler) { BaseAddress = new Uri("http://docker/"), Timeout = timeout };
            }

            case "tcp" or "http":
                return new HttpClient { BaseAddress = new UriBuilder("http", endpoint.Host, endpoint.Port).Uri, Timeout = timeout };

            default:
                throw new NotSupportedException($"Raw API access is not supported for '{endpoint.Scheme}' endpoints.");
        }
    }
}
