using Docker.DotNet;
using Stowaway.Core.Backups;
using Stowaway.Core.Inventory;
using Stowaway.Docker.Backups;
using Stowaway.Docker.Restore;

namespace Stowaway.Docker.Databases;

/// <summary>
/// Windows engines: SQL Server inside Windows containers (Business Central images and the like), whose databases live
/// inside the container where the volume backup can't see them. Running containers: <c>BACKUP DATABASE</c>; stopped
/// ones (all along, or by stop &amp; start): the database files are copied. Both land in the host staging folder's
/// <c>_dumps</c>. See <see cref="SqlServerDumps"/>.
/// </summary>
public sealed class SqlServerProvider(SqlServerDumpService dumps, SqlServerDrill drill) : IDatabaseProvider
{
    public IReadOnlyCollection<DatabaseKind> Kinds { get; } = [DatabaseKind.SqlServer];

    public string Platform => Platforms.Windows;

    public Task<ProbeResult> ProbeAsync(DockerClient client, string containerId, DatabaseKind kind, IReadOnlyList<string> environment, CancellationToken ct) =>
        dumps.ProbeAsync(client, containerId, environment, ct);

    public async Task<DumpOutcome> DumpAsync(DumpContext context, CancellationToken ct)
    {
        if (SqlServerDumps.Containers(context.Item).Count == 0)
        {
            return DumpOutcome.Nothing;
        }

        context.Phase("backing up SQL Server databases");
        var errors = await dumps.CreateAsync(context.Client, context.Item, context.Log, ct);
        return new DumpOutcome(errors, [], null, "SQL Server backup failed", "The snapshot doesn't include those databases.");
    }

    public Task VerifyAsync(DrillContext context, CancellationToken ct) =>
        drill.CheckAsync(context.Client, context.Volume, context.Log, context.Phase, context.Failures, ct);
}
