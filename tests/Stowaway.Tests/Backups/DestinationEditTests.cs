using Stowaway.Core.Backups;
using Stowaway.Core.Plans;
using Stowaway.Docker;
using Stowaway.Docker.Backups;
using Stowaway.Service.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Stowaway.Tests.Backups;

public sealed class DropboxCallbackTests
{
    [Theory]
    [InlineData("http://localhost:53682/?state=abc&code=xyz", "state=abc&code=xyz")]
    [InlineData("  http://127.0.0.1:53682/?code=x%2By&state=s  ", "state=s&code=x%2By")]
    [InlineData("http://localhost:53682/?state=s&error=access_denied&extra=1", "state=s&error=access_denied")]
    public void Pasted_callback_addresses_forward_only_the_oauth_parameters(string pasted, string query) =>
        Assert.Equal(query, DropboxAuthService.CallbackQuery(pasted));

    [Theory]
    [InlineData("https://localhost:53682/?state=s&code=c")] // not what rclone listens on
    [InlineData("http://evil.example:53682/?state=s&code=c")]
    [InlineData("http://localhost:8484/?state=s&code=c")]
    [InlineData("http://localhost:53682/?code=c")] // no state
    [InlineData("http://localhost:53682/?state=s")] // neither code nor error
    [InlineData("not a url")]
    public void Other_addresses_are_refused(string pasted) =>
        Assert.Null(DropboxAuthService.CallbackQuery(pasted));

    [Theory]
    [InlineData("https://www.dropbox.com/oauth2/authorize?client_id=x&state=s", true)]
    [InlineData("http://www.dropbox.com/oauth2/authorize?client_id=x", false)]
    [InlineData("https://www.dropbox.com.evil.example/oauth2/authorize", false)]
    [InlineData("https://www.dropbox.com/login", false)]
    public void Only_dropbox_sign_in_pages_are_handed_to_the_browser(string url, bool ok) =>
        Assert.Equal(ok, DropboxAuthService.IsDropboxAuthorizeUrl(new Uri(url)));
}

/// <summary>
/// Edits and removal of a local-folder destination through the real runner. Needs a Linux-container engine
/// (skipped otherwise). Uses only temporary folders.
/// </summary>
public sealed class DestinationEditTests
{
    [Fact]
    public async Task Edits_follow_a_moved_repository_and_refuse_different_backups()
    {
        var ct = TestContext.Current.CancellationToken;
        var monitor = new DockerEngineMonitor(Options.Create(new DockerEngineOptions()), TimeProvider.System, NullLogger<DockerEngineMonitor>.Instance);
        var status = await monitor.ProbeAsync(ct);
        Assert.SkipUnless(status.Reachable && status.OsType == "linux", "Needs a Linux-container Docker engine");

        var root = Directory.CreateTempSubdirectory("Stowaway.Edit.").FullName;
        try
        {
            var protector = new DpapiSecretProtector();
            var runner = new ResticRunner(monitor, Options.Create(new BackupOptions()), NullLogger<ResticRunner>.Instance);
            var repository = new ResticRepository(runner, protector, NullLogger<ResticRepository>.Instance);
            var store = new MemoryDestinations();
            var plans = new MemoryPlans();
            var service = new DestinationService(store, repository, protector, new NoEvents(), Stowaway.Docker.Destinations.DestinationProviders.BuiltIn(runner), plans, NullLogger<DestinationService>.Instance);

            var first = await service.CreateAsync(new CreateDestinationRequest("First", DestinationKind.Local, Folder(Path.Combine(root, "a"))), ct);
            var other = await service.CreateAsync(new CreateDestinationRequest("Other", DestinationKind.Local, Folder(Path.Combine(root, "b"))), ct);

            // Rename only: no location check needed.
            Assert.Equal("Renamed", (await service.UpdateAsync(first.Id, new UpdateDestinationRequest("Renamed"), ct)).Name);
            await Assert.ThrowsAsync<ValidationException>(() => service.UpdateAsync(first.Id, new UpdateDestinationRequest("other"), ct));

            // The repository was moved: the destination can follow it.
            Directory.Move(Path.Combine(root, "a"), Path.Combine(root, "moved"));
            var moved = await service.UpdateAsync(first.Id, new UpdateDestinationRequest(null, Folder(Path.Combine(root, "moved"))), ct);
            Assert.Equal(Path.Combine(root, "moved"), moved.Path);
            Assert.Equal(first.RepositoryId, moved.RepositoryId);

            // Pointing it at different backups (or nothing) is refused and changes nothing.
            await Assert.ThrowsAsync<ValidationException>(() => service.UpdateAsync(first.Id, new UpdateDestinationRequest(null, Folder(Path.Combine(root, "b"))), ct));
            await Assert.ThrowsAsync<ValidationException>(() => service.UpdateAsync(first.Id, new UpdateDestinationRequest(null, Folder(Path.Combine(root, "empty"))), ct));
            Assert.Equal(Path.Combine(root, "moved"), (await store.GetAsync(first.Id, ct))!.Path);

            // A destination a plan uses can't be removed; the message names the plan.
            plans.Items.Add(new BackupPlan { Name = "Nightly", DestinationId = other.Id, Cron = "0 2 * * *" });
            var refused = await Assert.ThrowsAsync<ValidationException>(() => service.DeleteAsync(other.Id, ct));
            Assert.Contains("Nightly", refused.Message, StringComparison.Ordinal);
            plans.Items.Clear();
            Assert.True(await service.DeleteAsync(other.Id, ct));
        }
        finally
        {
            // Creates and edits start a status check in the background; let those runners finish and remove
            // themselves so none is left behind when the test process exits.
            using var client = new global::Docker.DotNet.DockerClientBuilder().WithEndpoint(new Uri(status.Endpoint!)).Build();
            var runners = new global::Docker.DotNet.Models.ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>> { ["label"] = new Dictionary<string, bool> { [$"{ResticRunner.RunnerLabel}=true"] = true } },
            };
            for (var i = 0; i < 120 && (await client.Containers.ListContainersAsync(runners, CancellationToken.None)).Count > 0; i++)
            {
                await Task.Delay(500, CancellationToken.None);
            }

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal); // restic writes read-only files
            }

            Directory.Delete(root, recursive: true);
        }
    }

    private static Dictionary<string, string> Folder(string path) => new() { ["path"] = path };

    private sealed class MemoryDestinations : IDestinationStore
    {
        private readonly List<Destination> _items = [];
        public Task<IReadOnlyList<Destination>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Destination>>([.. _items]);
        public Task<Destination?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(_items.FirstOrDefault(d => d.Id == id));
        public Task AddAsync(Destination destination, CancellationToken ct)
        {
            _items.Add(destination);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(Destination destination, CancellationToken ct)
        {
            _items[_items.FindIndex(d => d.Id == destination.Id)] = destination;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(Guid id, CancellationToken ct) => Task.FromResult(_items.RemoveAll(d => d.Id == id) > 0);
    }

    private sealed class MemoryPlans : IPlanStore
    {
        public List<BackupPlan> Items { get; } = [];
        public Task<IReadOnlyList<BackupPlan>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<BackupPlan>>([.. Items]);
        public Task<BackupPlan?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Items.FirstOrDefault(p => p.Id == id));
        public Task AddAsync(BackupPlan plan, CancellationToken ct) => throw new NotSupportedException();
        public Task UpdateAsync(BackupPlan plan, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class NoEvents : IBackupEvents
    {
        public void RunUpdated(BackupRun run) { }
        public void Progress(BackupProgress progress) { }
        public void DestinationsChanged() { }
        public void PlansChanged() { }
    }
}
