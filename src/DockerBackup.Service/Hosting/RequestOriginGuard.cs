using System.Net;

namespace DockerBackup.Service.Hosting;

/// <summary>
/// Defends the loopback API against other websites open in the user's browser:
/// <list type="bullet">
/// <item>WebSocket/SignalR connections are not covered by CORS, so hub requests from a non-loopback Origin are refused.</item>
/// <item>State-changing API calls must carry <see cref="HeaderName"/>. A cross-site page can only add a custom
/// header after a CORS preflight, which this server never approves. Foreign Origins are refused outright.</item>
/// </list>
/// Host filtering (AllowedHosts) separately blocks DNS-rebinding attacks.
/// </summary>
public static class RequestOriginGuard
{
    public const string HeaderName = "X-DockerBackup-Request";

    public static IApplicationBuilder UseRequestOriginGuard(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var request = context.Request;
            var isHub = request.Path.StartsWithSegments("/hubs");
            var isApiWrite = request.Path.StartsWithSegments("/api")
                && !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method);

            if (isHub || isApiWrite)
            {
                var origin = request.Headers.Origin.ToString();
                if (origin.Length > 0 && !IsLoopbackOrigin(origin))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

                if (isApiWrite && request.Headers[HeaderName] != "1")
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
            }

            await next();
        });

    internal static bool IsLoopbackOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip)));
}
