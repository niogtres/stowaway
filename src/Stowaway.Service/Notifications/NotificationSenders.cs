using System.Text;
using Stowaway.Core.Notifications;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Stowaway.Service.Notifications;

/// <summary>Delivers one message through one channel kind.</summary>
public interface INotificationSender
{
    NotificationKind Kind { get; }

    Task SendAsync(string settingsJson, NotificationMessage message, CancellationToken ct);
}

public sealed class EmailSender : INotificationSender
{
    public NotificationKind Kind => NotificationKind.Email;

    public async Task SendAsync(string settingsJson, NotificationMessage message, CancellationToken ct)
    {
        var settings = NotificationJson.Read<EmailSettings>(settingsJson);
        var mail = new MimeMessage();
        mail.From.Add(MailboxAddress.Parse(settings.From));
        foreach (var to in settings.To.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            mail.To.Add(MailboxAddress.Parse(to));
        }

        mail.Subject = $"[Stowaway] {message.Title}";
        mail.Body = new TextPart("plain")
        {
            Text = message.Body + (message.Link is null ? string.Empty : $"\n\nOpen Stowaway: {message.Link}"),
        };

        using var client = new SmtpClient { Timeout = 30_000 };
        var security = settings.Security switch
        {
            SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
            SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            SmtpSecurity.None => SecureSocketOptions.None,
            _ => SecureSocketOptions.Auto,
        };
        await client.ConnectAsync(settings.Host, settings.Port, security, ct);
        if (!string.IsNullOrEmpty(settings.Username))
        {
            await client.AuthenticateAsync(settings.Username, settings.Password ?? string.Empty, ct);
        }

        await client.SendAsync(new SmtpSendRequest(mail), ct);
        await client.DisconnectAsync(true, ct);
    }
}

public sealed class WebhookSender(IHttpClientFactory httpFactory) : INotificationSender
{
    public NotificationKind Kind => NotificationKind.Webhook;

    public async Task SendAsync(string settingsJson, NotificationMessage message, CancellationToken ct)
    {
        var settings = NotificationJson.Read<WebhookSettings>(settingsJson);
        using var http = httpFactory.CreateClient("notifications");
        object payload = settings.Format == WebhookFormat.Slack
            ? new { text = $"*{message.Title}*\n{message.Body}" + (message.Link is null ? string.Empty : $"\n<{message.Link}|Open Stowaway>") }
            : new
            {
                source = "stowaway",
                @event = message.Event.ToString().ToLowerInvariant(),
                title = message.Title,
                body = message.Body,
                link = message.Link,
                host = Environment.MachineName,
                at = DateTimeOffset.UtcNow,
            };
        // Buffered (Content-Length) rather than chunked: many small webhook receivers don't support chunked bodies.
        using var content = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(settings.Url, content, ct);
        response.EnsureSuccessStatusCode();
    }
}

public sealed class NtfySender(IHttpClientFactory httpFactory) : INotificationSender
{
    public NotificationKind Kind => NotificationKind.Ntfy;

    public async Task SendAsync(string settingsJson, NotificationMessage message, CancellationToken ct)
    {
        var settings = NotificationJson.Read<NtfySettings>(settingsJson);
        using var http = httpFactory.CreateClient("notifications");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.ServerUrl.TrimEnd('/')}/{Uri.EscapeDataString(settings.Topic)}")
        {
            Content = new StringContent(message.Body, Encoding.UTF8, "text/plain"),
        };
        request.Headers.Add("Title", message.Title);
        request.Headers.Add("Tags", message.Event switch
        {
            NotificationEvent.Failure => "rotating_light",
            NotificationEvent.Recovery => "white_check_mark",
            NotificationEvent.Security => "lock",
            _ => "floppy_disk",
        });
        request.Headers.Add("Priority", message.Event is NotificationEvent.Failure or NotificationEvent.Security ? "high" : "default");
        if (!string.IsNullOrEmpty(settings.AccessToken))
        {
            request.Headers.Authorization = new("Bearer", settings.AccessToken);
        }

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }
}

internal static class NotificationJson
{
    private static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static T Read<T>(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidOperationException("Missing notification settings.");

    public static string Write<T>(T value) => System.Text.Json.JsonSerializer.Serialize(value, Options);
}
