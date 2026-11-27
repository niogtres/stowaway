using Stowaway.Core.Backups;
using Stowaway.Core.Inventory;

namespace Stowaway.Core.Databases;

/// <summary>PostgreSQL (and PostGIS, TimescaleDB): <c>pg_dumpall</c>, or <c>pg_dump</c> of one database when a login names one.</summary>
public static class PostgreSqlDatabase
{
    public static ShellDatabase Definition { get; } = new(
        [DatabaseKind.PostgreSql],
        "sql",
        Credentials:
            """
            f() { [ -n "$1" ] && cat "$1" 2>/dev/null; }
            DB_USER="${STOWAWAY_DB_USER:-${POSTGRES_USER:-$(f "$POSTGRES_USER_FILE")}}"
            DB_USER="${DB_USER:-postgres}"
            export PGPASSWORD="${STOWAWAY_DB_PASSWORD:-${POSTGRES_PASSWORD:-$(f "$POSTGRES_PASSWORD_FILE")}}"
            """,
        Dump:
            """
            if [ -n "$STOWAWAY_DB_NAME" ]; then exec pg_dump -U "$DB_USER" --clean --if-exists --create -d "$STOWAWAY_DB_NAME"; fi
            exec pg_dumpall -U "$DB_USER" --clean --if-exists
            """,
        Probe:
            """exec psql -U "$DB_USER" -d "${STOWAWAY_DB_NAME:-postgres}" -tAc 'select 1'""",
        Drill: file => new DrillCheck(
            [$"POSTGRES_PASSWORD={DrillChecks.Password}"],
            null,
            ["pg_isready", "-h", "127.0.0.1", "-U", "postgres"],
            ["sh", "-c", $"psql -U postgres -q -f {file} >/dev/null 2>/tmp/load.err; echo ok"],
            ["psql", "-U", "postgres", "-Atc", "select count(*) from pg_database where not datistemplate and datname <> 'postgres'"],
            "databases"));
}
