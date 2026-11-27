using Stowaway.Core.Inventory;

namespace Stowaway.Core.Backups;

/// <summary>A consistent, app-aware dump run inside a database container; its output is streamed into the backup.</summary>
public sealed record DumpCommand(ContainerInfo Container, DatabaseKind Kind, string FileName, IReadOnlyList<string> Command);

/// <summary>
/// Dump commands for common databases. They run in the database's own container (so the dump tool matches the
/// server version). Credentials come from Stowaway when the owner entered them for that container, otherwise
/// from the container's environment as the official images set it up (including <c>*_FILE</c> secrets).
/// </summary>
public static class DatabaseDumps
{
    /// <summary>Where dumps appear inside snapshots.</summary>
    public const string SnapshotDir = "/data/_dumps";

    /// <summary>
    /// Whether Stowaway can take a consistent copy of this database while it runs. Linux engines: shell-based dumps
    /// (<see cref="For"/>). Windows engines: SQL Server only (<see cref="SqlServerDumps"/>).
    /// </summary>
    public static bool Supported(DatabaseKind kind, string platform = Platforms.Linux) => platform == Platforms.Windows
        ? kind == DatabaseKind.SqlServer
        : kind is DatabaseKind.PostgreSql or DatabaseKind.MySql or DatabaseKind.MariaDb or DatabaseKind.MongoDb or DatabaseKind.Redis;

    /// <summary>Shell-based dumps for the item's running database containers (Linux engines).</summary>
    public static IReadOnlyList<DumpCommand> For(AppInfo item) =>
        item.Containers
            .Where(c => item.Platform == Platforms.Linux && c.State == "running" && c.Id.Length > 0 && c.Database is { } kind && Supported(kind))
            .Select(c => new DumpCommand(c, c.Database!.Value, $"{Safe(c.Name)}.{Extension(c.Database!.Value)}", ["sh", "-c", Script(c.Database!.Value)]))
            .ToList();

    /// <summary>Running database containers in the item that can't be dumped (they need stop &amp; start for consistency).</summary>
    public static IReadOnlyList<ContainerInfo> Undumpable(AppInfo item) =>
        item.Containers.Where(c => c.State == "running" && c.Database is { } kind && !Supported(kind, item.Platform)).ToList();

    /// <summary>Environment variables carrying credentials entered in Stowaway; they win over what the container's own environment says.</summary>
    public const string UserVariable = "STOWAWAY_DB_USER";
    public const string PasswordVariable = "STOWAWAY_DB_PASSWORD";
    public const string DatabaseVariable = "STOWAWAY_DB_NAME";

    /// <summary>
    /// The shell script that writes the dump to stdout. Line endings are normalised: the raw string literals take the
    /// source file's line endings, and a Windows checkout (CRLF) would hand <c>sh</c> "then\r" and "fi\r".
    /// </summary>
    internal static string Script(DatabaseKind kind) => (Credentials(kind) + "\n" + DumpPart(kind)).ReplaceLineEndings("\n");

    /// <summary>A quick login check with the same credentials as the dump: prints something and exits 0 when they work.</summary>
    public static IReadOnlyList<string> ProbeCommand(DatabaseKind kind) =>
        ["sh", "-c", (Credentials(kind) + "\n" + ProbePart(kind)).ReplaceLineEndings("\n")];

    /// <summary>
    /// Works out user and password: Stowaway's own (STOWAWAY_DB_*) first, then the official images' variables,
    /// including their <c>*_FILE</c> forms (Docker secrets), read inside the container.
    /// </summary>
    private static string Credentials(DatabaseKind kind) => kind switch
    {
        DatabaseKind.PostgreSql =>
            """
            f() { [ -n "$1" ] && cat "$1" 2>/dev/null; }
            DB_USER="${STOWAWAY_DB_USER:-${POSTGRES_USER:-$(f "$POSTGRES_USER_FILE")}}"
            DB_USER="${DB_USER:-postgres}"
            export PGPASSWORD="${STOWAWAY_DB_PASSWORD:-${POSTGRES_PASSWORD:-$(f "$POSTGRES_PASSWORD_FILE")}}"
            """,

        DatabaseKind.MySql or DatabaseKind.MariaDb =>
            """
            f() { [ -n "$1" ] && cat "$1" 2>/dev/null; }
            ROOT_PW="${MARIADB_ROOT_PASSWORD:-${MYSQL_ROOT_PASSWORD:-$(f "${MARIADB_ROOT_PASSWORD_FILE:-$MYSQL_ROOT_PASSWORD_FILE}")}}"
            if [ -n "$STOWAWAY_DB_USER" ]; then
              DB_USER="$STOWAWAY_DB_USER"; export MYSQL_PWD="$STOWAWAY_DB_PASSWORD"
              if [ -n "$STOWAWAY_DB_NAME" ]; then SCOPE="--databases $STOWAWAY_DB_NAME"; else SCOPE="--all-databases"; fi
            elif [ -n "$ROOT_PW" ] || [ -n "$MARIADB_ALLOW_EMPTY_ROOT_PASSWORD$MYSQL_ALLOW_EMPTY_PASSWORD" ]; then
              DB_USER=root; export MYSQL_PWD="$ROOT_PW"; SCOPE="--all-databases"
            else
              DB_USER="${MARIADB_USER:-$MYSQL_USER}"
              export MYSQL_PWD="${MARIADB_PASSWORD:-${MYSQL_PASSWORD:-$(f "${MARIADB_PASSWORD_FILE:-$MYSQL_PASSWORD_FILE}")}}"
              SCOPE="--databases ${MARIADB_DATABASE:-$MYSQL_DATABASE}"
            fi
            """,

        DatabaseKind.MongoDb =>
            """
            f() { [ -n "$1" ] && cat "$1" 2>/dev/null; }
            DB_USER="${STOWAWAY_DB_USER:-${MONGO_INITDB_ROOT_USERNAME:-$(f "$MONGO_INITDB_ROOT_USERNAME_FILE")}}"
            DB_PASS="${STOWAWAY_DB_PASSWORD:-${MONGO_INITDB_ROOT_PASSWORD:-$(f "$MONGO_INITDB_ROOT_PASSWORD_FILE")}}"
            """,

        DatabaseKind.Redis =>
            """
            f() { [ -n "$1" ] && cat "$1" 2>/dev/null; }
            DB_USER="$STOWAWAY_DB_USER"
            DB_PASS="${STOWAWAY_DB_PASSWORD:-${REDIS_PASSWORD:-$(f "$REDIS_PASSWORD_FILE")}}"
            """,

        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No dump support"),
    };

    private static string DumpPart(DatabaseKind kind) => kind switch
    {
        DatabaseKind.PostgreSql =>
            """
            if [ -n "$STOWAWAY_DB_NAME" ]; then exec pg_dump -U "$DB_USER" --clean --if-exists --create -d "$STOWAWAY_DB_NAME"; fi
            exec pg_dumpall -U "$DB_USER" --clean --if-exists
            """,

        DatabaseKind.MySql or DatabaseKind.MariaDb =>
            """
            DUMP="$(command -v mariadb-dump || command -v mysqldump)"
            exec "$DUMP" -u"$DB_USER" $SCOPE --single-transaction --routines --events --triggers
            """,

        DatabaseKind.MongoDb =>
            """
            if [ -z "$DB_USER" ]; then exec mongodump --archive --quiet; fi
            if [ -n "$STOWAWAY_DB_NAME" ]; then
              exec mongodump --archive --quiet --username "$DB_USER" --password "$DB_PASS" --authenticationDatabase admin --db "$STOWAWAY_DB_NAME"
            fi
            exec mongodump --archive --quiet --username "$DB_USER" --password "$DB_PASS" --authenticationDatabase admin
            """,

        DatabaseKind.Redis =>
            """exec redis-cli ${DB_USER:+--user "$DB_USER"} ${DB_PASS:+-a "$DB_PASS"} --no-auth-warning --rdb -""",

        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No dump support"),
    };

    private static string ProbePart(DatabaseKind kind) => kind switch
    {
        DatabaseKind.PostgreSql =>
            """exec psql -U "$DB_USER" -d "${STOWAWAY_DB_NAME:-postgres}" -tAc 'select 1'""",

        DatabaseKind.MySql or DatabaseKind.MariaDb =>
            """
            CLIENT="$(command -v mariadb || command -v mysql)"
            exec "$CLIENT" -u"$DB_USER" -N -e 'select 1'
            """,

        DatabaseKind.MongoDb =>
            """
            SHELL_BIN="$(command -v mongosh || command -v mongo)"
            if [ -z "$DB_USER" ]; then exec "$SHELL_BIN" --quiet --eval 'db.runCommand({ ping: 1 }).ok'; fi
            exec "$SHELL_BIN" --quiet --username "$DB_USER" --password "$DB_PASS" --authenticationDatabase admin --eval 'db.runCommand({ ping: 1 }).ok'
            """,

        DatabaseKind.Redis =>
            """exec redis-cli ${DB_USER:+--user "$DB_USER"} ${DB_PASS:+-a "$DB_PASS"} --no-auth-warning ping""",

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
