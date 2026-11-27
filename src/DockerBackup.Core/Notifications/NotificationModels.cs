namespace DockerBackup.Core.Notifications;

public enum NotificationKind
{
    Email,

    /// <summary>HTTP POST: generic JSON, or Slack-compatible (works for Slack, Discord's /slack endpoint, Mattermost).</summary>
    Webhook,

    /// <summary>ntfy.sh or a self-hosted ntfy server.</summary>
    Ntfy,
}

public enum WebhookFormat
{
    Json,
    Slack,
}

public enum SmtpSecurity
{
    /// <summary>Upgrade with STARTTLS when the server offers it.</summary>
    Auto,
    StartTls,
    SslOnConnect,
    None,
}

public sealed record EmailSettings(string Host, int Port, SmtpSecurity Security, string? Username, string? Password, string From, string To);

public sealed record WebhookSettings(string Url, WebhookFormat Format);

public sealed record NtfySettings(string ServerUrl, string Topic, string? AccessToken);

/// <summary>Where alerts go. The kind-specific settings (which can hold secrets) are stored DPAPI-protected.</summary>
public sealed class NotificationChannel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required NotificationKind Kind { get; set; }
    public bool Enabled { get; set; } = true;
    public bool OnFailure { get; set; } = true;

    /// <summary>A success after a failure.</summary>
    public bool OnRecovery { get; set; } = true;
    public bool OnSuccess { get; set; }

    /// <summary>DPAPI-protected JSON of the kind's settings record.</summary>
    public required string ProtectedSettings { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum NotificationEvent
{
    Failure,
    Recovery,
    Success,
    Test,

    /// <summary>Remote access: a sign-in from a new address, a lockout, a recovery code used. Sent where failures go.</summary>
    Security,
}

/// <summary>What to tell people. Senders format it for their medium.</summary>
public sealed record NotificationMessage(NotificationEvent Event, string Title, string Body, string? Link);

public interface INotificationStore
{
    Task<IReadOnlyList<NotificationChannel>> ListAsync(CancellationToken ct);
    Task<NotificationChannel?> GetAsync(Guid id, CancellationToken ct);
    Task AddAsync(NotificationChannel channel, CancellationToken ct);
    Task UpdateAsync(NotificationChannel channel, CancellationToken ct);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
}
