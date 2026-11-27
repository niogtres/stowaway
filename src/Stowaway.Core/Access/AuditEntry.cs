namespace Stowaway.Core.Access;

/// <summary>
/// One line of the audit log: a sign-in event, or a change made through the API (who, from where, what, result).
/// Request bodies are never recorded, so secrets can't end up here.
/// </summary>
public sealed class AuditEntry
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }

    /// <summary>Windows account (DOMAIN\user) or remote-access user name.</summary>
    public string Actor { get; set; } = "";

    /// <summary>Client address; behind a trusted proxy, the address it forwarded.</summary>
    public string Address { get; set; } = "";

    public bool Remote { get; set; }

    /// <summary>"Signed in", "Locked out", or for changes the method and path, e.g. "DELETE /api/plans/…".</summary>
    public string Action { get; set; } = "";

    public string Detail { get; set; } = "";

    /// <summary>HTTP status of a change; null for sign-in events.</summary>
    public int? Status { get; set; }
}

public interface IAuditStore
{
    Task AddAsync(AuditEntry entry, CancellationToken ct);
    Task<IReadOnlyList<AuditEntry>> ListRecentAsync(int limit, CancellationToken ct);
    Task<int> PruneAsync(DateTimeOffset olderThan, CancellationToken ct);
}
