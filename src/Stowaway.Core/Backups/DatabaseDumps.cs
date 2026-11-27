using Stowaway.Core.Inventory;

namespace Stowaway.Core.Backups;

/// <summary>A consistent, app-aware dump run inside a database container; its output is streamed into the backup.</summary>
public sealed record DumpCommand(ContainerInfo Container, DatabaseKind Kind, string FileName, IReadOnlyList<string> Command);

/// <summary>
/// Dump commands for common databases. They run in the database's own container (so the dump tool matches the
/// server version) and read credentials from the container's environment, as set up by the official images.
/// </summary>
public static class DatabaseDumps
{
    /// <summary>Where dumps appear inside snapshots.</summary>
    public const string SnapshotDir = "/data/_dumps";

    public static bool Supported(DatabaseKind kind) =>
        kind is DatabaseKind.PostgreSql or DatabaseKind.MySql or DatabaseKind.MariaDb or DatabaseKind.MongoDb or DatabaseKind.Redis;

    /// <summary>Dumps for the item's running database containers.</summary>
    public static IReadOnlyList<DumpCommand> For(AppInfo item) =>
        item.Containers
            .Where(c => c.State == "running" && c.Id.Length > 0 && c.Database is { } kind && Supported(kind))
            .Select(c => new DumpCommand(c, c.Database!.Value, $"{Safe(c.Name)}.{Extension(c.Database!.Value)}", ["sh", "-c", Script(c.Database!.Value)]))
            .ToList();

    /// <summary>Running database containers in the item that can't be dumped (they need stop &amp; start for consistency).</summary>
    public static IReadOnlyList<ContainerInfo> Undumpable(AppInfo item) =>
        item.Containers.Where(c => c.State == "running" && c.Database is { } kind && !Supported(kind)).ToList();

    /// <summary>
    /// The shell script that writes the dump to stdout. Line endings are normalised: the raw string literals take the
    /// source file's line endings, and a Windows checkout (CRLF) would hand <c>sh</c> "then\r" and "fi\r".
    /// </summary>
    internal static string Script(DatabaseKind kind) => ScriptSource(kind).ReplaceLineEndings("\n");

    private static string ScriptSource(DatabaseKind kind) => kind switch
    {
        DatabaseKind.PostgreSql =>
            """export PGPASSWORD="$POSTGRES_PASSWORD"; exec pg_dumpall -U "${POSTGRES_USER:-postgres}" --clean --if-exists""",

        DatabaseKind.MySql or DatabaseKind.MariaDb =>
            """
            ROOT_PW="${MARIADB_ROOT_PASSWORD:-$MYSQL_ROOT_PASSWORD}"
            if [ -n "$ROOT_PW" ] || [ -n "$MARIADB_ALLOW_EMPTY_ROOT_PASSWORD$MYSQL_ALLOW_EMPTY_PASSWORD" ]; then
              DB_USER=root; export MYSQL_PWD="$ROOT_PW"; SCOPE="--all-databases"
            else
              DB_USER="${MARIADB_USER:-$MYSQL_USER}"; export MYSQL_PWD="${MARIADB_PASSWORD:-$MYSQL_PASSWORD}"; SCOPE="--databases ${MARIADB_DATABASE:-$MYSQL_DATABASE}"
            fi
            DUMP="$(command -v mariadb-dump || command -v mysqldump)"
            exec "$DUMP" -u"$DB_USER" $SCOPE --single-transaction --routines --events --triggers
            """,

        DatabaseKind.MongoDb =>
            """
            if [ -n "$MONGO_INITDB_ROOT_USERNAME" ]; then
              exec mongodump --archive --quiet --username "$MONGO_INITDB_ROOT_USERNAME" --password "$MONGO_INITDB_ROOT_PASSWORD" --authenticationDatabase admin
            fi
            exec mongodump --archive --quiet
            """,

        DatabaseKind.Redis =>
            """exec redis-cli ${REDIS_PASSWORD:+-a "$REDIS_PASSWORD"} --no-auth-warning --rdb -""",

        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No dump support"),
    };

    private static string Extension(DatabaseKind kind) => kind switch
    {
        DatabaseKind.MongoDb => "archive",
        DatabaseKind.Redis => "rdb",
        _ => "sql",
    };

    private static string Safe(string name) =>
        new(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
}
