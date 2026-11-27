using System.Net;
using System.Text.Json;
using DockerBackup.Core.Backups;
using DockerBackup.Core.Notifications;
using DockerBackup.Core.Plans;
using DockerBackup.Docker.Backups;
using DockerBackup.Service.Hosting;
using DockerBackup.Service.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace DockerBackup.Tests.Plans;

public sealed class NotificationTests
{
    private static ChannelInput Email(string to = "ops@example.com", string? password = "pw") => new(
        "Mail", NotificationKind.Email, true, true, true, false,
        new EmailSettings("smtp.example.com", 587, SmtpSecurity.StartTls, "user", password, "backup@example.com", to), null, null);

    [Fact]
    public void Validation_rejects_bad_settings()
    {
        Assert.Throws<ValidationException>(() => NotificationService.Validate(Email(to: "not-an-address"), null));
        Assert.Throws<ValidationException>(() => NotificationService.Validate(
            new ChannelInput("Hook", NotificationKind.Webhook, true, true, true, false, null, new WebhookSettings("ftp://x", WebhookFormat.Json), null), null));
        Assert.Throws<ValidationException>(() => NotificationService.Validate(
            new ChannelInput("ntfy", NotificationKind.Ntfy, true, true, true, false, null, null, new NtfySettings("https://ntfy.sh", "bad topic!", null)), null));
    }

    [Fact]
    public void Editing_without_retyping_a_secret_keeps_the_stored_one()
    {
        var stored = NotificationService.Validate(Email(password: "original"), null);
        var updated = NotificationService.Validate(Email(password: ""), stored);
        Assert.Contains("original", updated);

        var replaced = NotificationService.Validate(Email(password: "new"), stored);
        Assert.Contains("\"new\"", replaced);
    }

    [Fact]
    public async Task Describes_failure_recovery_and_success()
    {
        var destinationId = Guid.NewGuid();
        var failed = new BackupRun { AppId = "compose:blog", AppName = "blog", DestinationId = destinationId, Strategy = BackupStrategy.Live, Status = RunStatus.Failed, Error = "disk full", Trigger = "schedule", PlanId = Guid.NewGuid() };
        var ok = new BackupRun { AppId = "compose:blog", AppName = "blog", DestinationId = destinationId, Strategy = BackupStrategy.Live, Status = RunStatus.Succeeded, Trigger = "schedule", PlanId = failed.PlanId, FilesProcessed = 10, BytesAdded = 2048 };

        var runs = new FakeRuns(previous: failed);
        var service = Service(runs, destinationId);

        var failure = await service.DescribeAsync(failed, CancellationToken.None);
        Assert.Equal(NotificationEvent.Failure, failure!.Event);
        Assert.Contains("disk full", failure.Body);
        Assert.Contains("NAS", failure.Body);

        Assert.Equal(NotificationEvent.Recovery, (await service.DescribeAsync(ok, CancellationToken.None))!.Event);

        runs.Previous = ok;
        var success = await service.DescribeAsync(ok, CancellationToken.None);
        Assert.Equal(NotificationEvent.Success, success!.Event);
        Assert.Contains("2 KB new data", success.Body);

        Assert.Null(await service.DescribeAsync(new BackupRun { AppId = "x", AppName = "x", DestinationId = destinationId, Strategy = BackupStrategy.Live, Status = RunStatus.Cancelled }, CancellationToken.None));
    }

    [Fact]
    public async Task Webhook_and_ntfy_payloads()
    {
        var handler = new CaptureHandler();
        var factory = new SingleClientFactory(handler);
        var message = new NotificationMessage(NotificationEvent.Failure, "Backup failed: blog", "disk full", "https://localhost:8484/activity");

        await new WebhookSender(factory).SendAsync("""{"url":"https://hooks.example.com/x","format":"Slack"}""", message, CancellationToken.None);
        Assert.Equal("https://hooks.example.com/x", handler.Last!.RequestUri!.ToString());
        Assert.Contains("*Backup failed: blog*", JsonDocument.Parse(handler.LastBody!).RootElement.GetProperty("text").GetString());

        await new WebhookSender(factory).SendAsync("""{"url":"https://hooks.example.com/y","format":"Json"}""", message, CancellationToken.None);
        Assert.NotNull(handler.Last.Content!.Headers.ContentLength); // buffered, not chunked
        var json = JsonDocument.Parse(handler.LastBody!).RootElement;
        Assert.Equal("failure", json.GetProperty("event").GetString());
        Assert.Equal("dockerbackup", json.GetProperty("source").GetString());

        await new NtfySender(factory).SendAsync("""{"serverUrl":"https://ntfy.sh/","topic":"my_backups","accessToken":"tk"}""", message, CancellationToken.None);
        Assert.Equal("https://ntfy.sh/my_backups", handler.Last!.RequestUri!.ToString());
        Assert.Equal("Backup failed: blog", handler.Last.Headers.GetValues("Title").Single());
        Assert.Equal("high", handler.Last.Headers.GetValues("Priority").Single());
        Assert.Equal("Bearer", handler.Last.Headers.Authorization!.Scheme);
        Assert.Equal("disk full", handler.LastBody);
    }

    private static NotificationService Service(IBackupRunStore runs, Guid destinationId) => new(
        new EmptyChannels(), new DpapiSecretProtector(), [], runs, new OneDestination(destinationId), new ServiceOptions(), NullLogger<NotificationService>.Instance);

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class EmptyChannels : INotificationStore
    {
        public Task<IReadOnlyList<NotificationChannel>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<NotificationChannel>>([]);
        public Task<NotificationChannel?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult<NotificationChannel?>(null);
        public Task AddAsync(NotificationChannel channel, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateAsync(NotificationChannel channel, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> DeleteAsync(Guid id, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class OneDestination(Guid id) : IDestinationStore
    {
        private readonly Destination _destination = new() { Id = id, Name = "NAS", Kind = DestinationKind.Smb, Path = @"\\nas\b", ProtectedRepositoryPassword = "x" };
        public Task<IReadOnlyList<Destination>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Destination>>([_destination]);
        public Task<Destination?> GetAsync(Guid destinationId, CancellationToken ct) => Task.FromResult<Destination?>(destinationId == id ? _destination : null);
        public Task AddAsync(Destination destination, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateAsync(Destination destination, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> DeleteAsync(Guid destinationId, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class FakeRuns(BackupRun? previous) : IBackupRunStore
    {
        public BackupRun? Previous { get; set; } = previous;
        public Task<BackupRun?> PreviousAsync(BackupRun run, CancellationToken ct) => Task.FromResult(Previous);
        public Task AddAsync(BackupRun run, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateAsync(BackupRun run, CancellationToken ct) => Task.CompletedTask;
        public Task<BackupRun?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult<BackupRun?>(null);
        public Task<IReadOnlyList<BackupRun>> ListRecentAsync(int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<BackupRun>>([]);
        public Task<int> FailInterruptedAsync(string reason, CancellationToken ct) => Task.FromResult(0);
        public Task<IReadOnlyDictionary<string, ItemRunState>> LatestStatesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<string, ItemRunState>>(new Dictionary<string, ItemRunState>());
        public Task<IReadOnlyDictionary<Guid, DateTimeOffset>> LastMaintenanceAsync(CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<Guid, DateTimeOffset>>(new Dictionary<Guid, DateTimeOffset>());
    }
}
