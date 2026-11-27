namespace Stowaway.Core.Inventory;

/// <summary>Recognises common database images so the app can suggest app-aware (dump-based) backups.</summary>
public static class DatabaseDetector
{
    // Matched against the image's repository name without registry or tag, e.g. "bitnami/postgresql".
    private static readonly (string Token, DatabaseKind Kind)[] Rules =
    [
        ("postgres", DatabaseKind.PostgreSql),
        ("postgis", DatabaseKind.PostgreSql),
        ("timescaledb", DatabaseKind.PostgreSql),
        ("mariadb", DatabaseKind.MariaDb),
        ("mysql", DatabaseKind.MySql),
        ("percona", DatabaseKind.MySql),
        ("mongo", DatabaseKind.MongoDb),
        ("redis", DatabaseKind.Redis),
        ("valkey", DatabaseKind.Redis),
        ("keydb", DatabaseKind.Redis),
        ("mssql", DatabaseKind.SqlServer),
        ("azure-sql-edge", DatabaseKind.SqlServer),
        ("elasticsearch", DatabaseKind.Elasticsearch),
        ("opensearch", DatabaseKind.Elasticsearch),
        ("influxdb", DatabaseKind.InfluxDb),
        ("couchdb", DatabaseKind.CouchDb),
    ];

    // Images whose names contain a database token but aren't databases.
    private static readonly string[] NotDatabases = ["phpmyadmin", "adminer", "pgadmin", "mongo-express", "redis-commander", "redisinsight", "exporter", "postgrest", "pgbouncer", "pgbackrest"];

    /// <summary>
    /// The database this container serves, if any. <paramref name="command"/> is the container's command line: a database
    /// image running its own shell script that never starts the server (a backup or migration sidecar running
    /// <c>pg_dump</c> against another container, say) is a client, not a database.
    /// </summary>
    public static DatabaseKind? Detect(string image, string? command = null) =>
        IsClientScript(command) ? null : DetectImage(image);

    private static readonly System.Text.RegularExpressions.Regex Shell = new(
        @"^\s*(/usr)?(/bin/)?(sh|bash|ash|dash)\s+(-\w+\s+)*-\w*c\b", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // What a wrapped server start looks like inside such a script.
    private static readonly System.Text.RegularExpressions.Regex ServerStart = new(
        @"docker-entrypoint|\b(mysqld|mariadbd|mongod|redis-server|valkey-server|keydb-server|sqlservr|elasticsearch|opensearch|influxd|couchdb)\b|(^|[;&|\n'""]\s*|\bexec\s+)(/usr/lib/postgresql/\S+/bin/)?postgres(\s|$|'|"")",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static bool IsClientScript(string? command) =>
        command is not null && Shell.IsMatch(command) && !ServerStart.IsMatch(Shell.Replace(command, "", 1));

    private static DatabaseKind? DetectImage(string image)
    {
        var repository = Repository(image);
        if (repository.Length == 0 || NotDatabases.Any(n => repository.Contains(n, StringComparison.Ordinal)))
        {
            return null;
        }

        foreach (var (token, kind) in Rules)
        {
            if (repository.Contains(token, StringComparison.Ordinal))
            {
                return kind;
            }
        }

        return null;
    }

    /// <summary>"registry.example.com:5000/team/postgres:17@sha256:…" → "team/postgres".</summary>
    internal static string Repository(string image)
    {
        var name = image.Trim().ToLowerInvariant();
        var at = name.IndexOf('@');
        if (at >= 0)
        {
            name = name[..at];
        }

        var lastSlash = name.LastIndexOf('/');
        var colon = name.LastIndexOf(':');
        if (colon > lastSlash)
        {
            name = name[..colon];
        }

        var parts = name.Split('/');
        if (parts.Length > 1 && (parts[0].Contains('.') || parts[0].Contains(':') || parts[0] == "localhost"))
        {
            name = string.Join('/', parts[1..]);
        }

        return name;
    }
}
