using System.Security.Cryptography;
using Stowaway.Core.Backups;
using Stowaway.Data;
using Stowaway.Service.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Stowaway.Tests.Backups;

public sealed class PersistenceTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("Stowaway.Db.").FullName;
    private ServiceProvider _services = null!;

    public async ValueTask InitializeAsync()
    {
        _services = new ServiceCollection().AddStowawayData(Path.Combine(_dir, "test.db")).BuildServiceProvider();
        await _services.MigrateDatabaseAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    private static Destination NewDestination(string name = "Disk") => new()
    {
        Name = name, Kind = DestinationKind.Local, Path = @"D:\Backups", ProtectedRepositoryPassword = "dpapi1:x",
    };

    [Fact]
    public async Task Destinations_round_trip_and_names_are_unique()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = _services.GetRequiredService<IDestinationStore>();
        var destination = NewDestination();
        await store.AddAsync(destination, ct);

        var loaded = await store.GetAsync(destination.Id, ct);
        Assert.Equal(DestinationKind.Local, loaded!.Kind);
        Assert.Equal(@"D:\Backups", loaded.Path);

        await Assert.ThrowsAnyAsync<Exception>(() => store.AddAsync(NewDestination(), ct));
        Assert.True(await store.DeleteAsync(destination.Id, ct));
        Assert.Empty(await store.ListAsync(ct));
    }

    [Fact]
    public async Task Runs_are_listed_newest_first_and_interrupted_runs_are_failed()
    {
        var ct = TestContext.Current.CancellationToken;
        var destination = NewDestination();
        await _services.GetRequiredService<IDestinationStore>().AddAsync(destination, ct);
        var runs = _services.GetRequiredService<IBackupRunStore>();

        var start = DateTimeOffset.UtcNow;
        var older = new BackupRun { AppId = "a", AppName = "a", DestinationId = destination.Id, Strategy = BackupStrategy.Live, QueuedAt = start, Status = RunStatus.Succeeded };
        var newer = new BackupRun { AppId = "b", AppName = "b", DestinationId = destination.Id, Strategy = BackupStrategy.StopStart, QueuedAt = start.AddMinutes(1), Status = RunStatus.Running };
        await runs.AddAsync(older, ct);
        await runs.AddAsync(newer, ct);

        Assert.Equal([newer.Id, older.Id], (await runs.ListRecentAsync(10, ct)).Select(r => r.Id));

        Assert.Equal(1, await runs.FailInterruptedAsync("restarted", ct));
        var failed = await runs.GetAsync(newer.Id, ct);
        Assert.Equal(RunStatus.Failed, failed!.Status);
        Assert.Equal("restarted", failed.Error);
        Assert.Equal(RunStatus.Succeeded, (await runs.GetAsync(older.Id, ct))!.Status);
    }

    [Fact]
    public void Secrets_round_trip_and_tampering_is_detected()
    {
        var protector = new DpapiSecretProtector();
        var protectedText = protector.Protect("s3cret, with comma");
        Assert.StartsWith("dpapi1:", protectedText);
        Assert.DoesNotContain("s3cret", protectedText);
        Assert.Equal("s3cret, with comma", protector.Unprotect(protectedText));
        Assert.Throws<CryptographicException>(() => protector.Unprotect("plain"));
    }
}
