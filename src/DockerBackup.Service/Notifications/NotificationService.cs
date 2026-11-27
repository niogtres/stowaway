using System.Globalization;
using DockerBackup.Core.Backups;
using DockerBackup.Core.Notifications;
using DockerBackup.Docker.Backups;
using DockerBackup.Service.Hosting;
using MimeKit;

namespace DockerBackup.Service.Notifications;

public sealed record ChannelInput(
    string Name,
    NotificationKind Kind,
    bool Enabled,
    bool OnFailure,
    bool OnRecovery,
    bool OnSuccess,
    EmailSettings? Email,
    WebhookSettings? Webhook,
    NtfySettings? Ntfy);

/// <summary>A channel as shown to the UI: never includes passwords or tokens.</summary>
public sealed record ChannelView(
    Guid Id,
    string Name,
    NotificationKind Kind,
    bool Enabled,
    bool OnFailure,
    bool OnRecovery,
    bool OnSuccess,
    string Target,
    DateTimeOffset CreatedAt);

/// <summary>Manages notification channels and sends alerts when runs finish.</summary>
public sealed class NotificationService(
    INotificationStore store,
    ISecretProtector secrets,
    IEnumerable<INotificationSender> senders,
    IBackupRunStore runs,
    IDestinationStore destinations,
    ServiceOptions serviceOptions,
    ILogger<NotificationService> logger) : IRunObserver
{
    private string Link => $"https://localhost:{serviceOptions.Port}/activity";

    public async Task<IReadOnlyList<ChannelView>> ListAsync(CancellationToken ct) =>
        (await store.ListAsync(ct)).Select(ToView).ToList();

    public async Task<ChannelView> CreateAsync(ChannelInput input, CancellationToken ct)
    {
        var settingsJson = Validate(input, existing: null);
        await EnsureUniqueNameAsync(input.Name, null, ct);
        var channel = new NotificationChannel
        {
            Name = input.Name.Trim(),
            Kind = input.Kind,
            Enabled = input.Enabled,
            OnFailure = input.OnFailure,
            OnRecovery = input.OnRecovery,
            OnSuccess = input.OnSuccess,
            ProtectedSettings = secrets.Protect(settingsJson),
        };
        await store.AddAsync(channel, ct);
        return ToView(channel);
    }

    public async Task<ChannelView> UpdateAsync(Guid id, ChannelInput input, CancellationToken ct)
    {
        var channel = await store.GetAsync(id, ct) ?? throw new KeyNotFoundException();
        if (channel.Kind != input.Kind)
        {
            throw new ValidationException("A channel's type can't be changed; create a new channel instead.");
        }

        var settingsJson = Validate(input, secrets.Unprotect(channel.ProtectedSettings));
        await EnsureUniqueNameAsync(input.Name, id, ct);
        channel.Name = input.Name.Trim();
        channel.Enabled = input.Enabled;
        channel.OnFailure = input.OnFailure;
        channel.OnRecovery = input.OnRecovery;
        channel.OnSuccess = input.OnSuccess;
        channel.ProtectedSettings = secrets.Protect(settingsJson);
        await store.UpdateAsync(channel, ct);
        return ToView(channel);
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken ct) => store.DeleteAsync(id, ct);

    /// <summary>Sends a test message; returns null on success or the error text.</summary>
    public async Task<string?> TestAsync(Guid id, CancellationToken ct)
    {
        var channel = await store.GetAsync(id, ct) ?? throw new KeyNotFoundException();
        try
        {
            await SendAsync(channel, new NotificationMessage(NotificationEvent.Test, "Test notification",
                $"Notifications from DockerBackup on {Environment.MachineName} reach this channel.", Link), ct);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ex.GetBaseException().Message;
        }
    }

    public async Task RunFinishedAsync(BackupRun run, CancellationToken ct)
    {
        var message = await DescribeAsync(run, ct);
        if (message is null)
        {
            return;
        }

        foreach (var channel in await store.ListAsync(ct))
        {
            var wanted = channel.Enabled && message.Event switch
            {
                NotificationEvent.Failure => channel.OnFailure,
                NotificationEvent.Recovery => channel.OnRecovery || channel.OnSuccess,
                NotificationEvent.Success => channel.OnSuccess,
                _ => false,
            };
            if (!wanted)
            {
                continue;
            }

            try
            {
                await SendAsync(channel, message, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Notification via {Channel} failed", channel.Name);
            }
        }
    }

    internal async Task<NotificationMessage?> DescribeAsync(BackupRun run, CancellationToken ct)
    {
        if (run.Status is not (RunStatus.Succeeded or RunStatus.Failed))
        {
            return null;
        }

        var destination = (await destinations.GetAsync(run.DestinationId, ct))?.Name ?? "a removed destination";
        var what = run.Kind == RunKind.Maintenance ? $"Maintenance of {destination}" : $"Backup of {run.AppName} to {destination}";
        var trigger = run.Trigger switch { "schedule" => "scheduled", "catch-up" => "catch-up after a missed schedule", "run-now" => "plan run now", _ => run.Trigger };

        if (run.Status == RunStatus.Failed)
        {
            return new NotificationMessage(NotificationEvent.Failure,
                run.Kind == RunKind.Maintenance ? $"Maintenance failed: {destination}" : $"Backup failed: {run.AppName}",
                $"{what} ({trigger}) failed on {Environment.MachineName}.\n\n{run.Error}", Link);
        }

        var previous = await runs.PreviousAsync(run, ct);
        var detail = run.Kind == RunKind.Maintenance
            ? "No errors found."
            : string.Create(CultureInfo.InvariantCulture, $"{run.FilesProcessed:N0} files, {Bytes(run.BytesAdded)} new data.");
        return previous?.Status == RunStatus.Failed
            ? new NotificationMessage(NotificationEvent.Recovery, $"Recovered: {run.AppName}", $"{what} ({trigger}) succeeded again. {detail}", Link)
            : new NotificationMessage(NotificationEvent.Success, $"Backed up: {run.AppName}", $"{what} ({trigger}) succeeded. {detail}", Link);
    }

    private async Task SendAsync(NotificationChannel channel, NotificationMessage message, CancellationToken ct)
    {
        var sender = senders.FirstOrDefault(s => s.Kind == channel.Kind) ?? throw new InvalidOperationException($"No sender for {channel.Kind}.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await sender.SendAsync(secrets.Unprotect(channel.ProtectedSettings), message, timeout.Token);
    }

    private ChannelView ToView(NotificationChannel c) =>
        new(c.Id, c.Name, c.Kind, c.Enabled, c.OnFailure, c.OnRecovery, c.OnSuccess, Target(c), c.CreatedAt);

    private string Target(NotificationChannel c)
    {
        try
        {
            var json = secrets.Unprotect(c.ProtectedSettings);
            return c.Kind switch
            {
                NotificationKind.Email => NotificationJson.Read<EmailSettings>(json).To,
                NotificationKind.Webhook => new Uri(NotificationJson.Read<WebhookSettings>(json).Url).Host,
                NotificationKind.Ntfy => NotificationJson.Read<NtfySettings>(json) is var n ? $"{new Uri(n.ServerUrl).Host}/{n.Topic}" : string.Empty,
                _ => string.Empty,
            };
        }
        catch (Exception)
        {
            return "(unreadable settings)";
        }
    }

    private async Task EnsureUniqueNameAsync(string name, Guid? id, CancellationToken ct)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > 100)
        {
            throw new ValidationException("Give the channel a name (up to 100 characters).");
        }

        if ((await store.ListAsync(ct)).Any(c => c.Id != id && string.Equals(c.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ValidationException($"A channel named '{trimmed}' already exists.");
        }
    }

    /// <summary>Validates kind-specific settings and returns their JSON. Blank secrets keep the existing ones.</summary>
    internal static string Validate(ChannelInput input, string? existing)
    {
        switch (input.Kind)
        {
            case NotificationKind.Email:
            {
                var e = input.Email ?? throw new ValidationException("Enter the email settings.");
                if (string.IsNullOrWhiteSpace(e.Host) || e.Port is < 1 or > 65535)
                {
                    throw new ValidationException("Enter the SMTP server and port.");
                }

                foreach (var address in e.To.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Append(e.From))
                {
                    if (!MailboxAddress.TryParse(address, out _) || !address.Contains('@'))
                    {
                        throw new ValidationException($"'{address}' isn't a valid email address.");
                    }
                }

                var password = string.IsNullOrEmpty(e.Password) && existing is not null ? NotificationJson.Read<EmailSettings>(existing).Password : e.Password;
                return NotificationJson.Write(e with { Host = e.Host.Trim(), Password = password });
            }

            case NotificationKind.Webhook:
            {
                var w = input.Webhook ?? throw new ValidationException("Enter the webhook URL.");
                RequireHttpUrl(w.Url);
                return NotificationJson.Write(w with { Url = w.Url.Trim() });
            }

            case NotificationKind.Ntfy:
            {
                var n = input.Ntfy ?? throw new ValidationException("Enter the ntfy settings.");
                RequireHttpUrl(n.ServerUrl);
                if (string.IsNullOrWhiteSpace(n.Topic) || n.Topic.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')))
                {
                    throw new ValidationException("The ntfy topic may only contain letters, digits, '-' and '_'.");
                }

                var token = string.IsNullOrEmpty(n.AccessToken) && existing is not null ? NotificationJson.Read<NtfySettings>(existing).AccessToken : n.AccessToken;
                return NotificationJson.Write(n with { ServerUrl = n.ServerUrl.Trim(), Topic = n.Topic.Trim(), AccessToken = token });
            }

            default:
                throw new ValidationException("Unknown channel type.");
        }
    }

    private static void RequireHttpUrl(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            throw new ValidationException("Enter a full http(s) URL.");
        }
    }

    private static string Bytes(long? bytes)
    {
        double value = bytes ?? 0;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }
}
