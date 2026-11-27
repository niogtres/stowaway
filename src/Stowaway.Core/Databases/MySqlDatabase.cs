using Stowaway.Core.Backups;
using Stowaway.Core.Inventory;

namespace Stowaway.Core.Databases;

/// <summary>MySQL, MariaDB and Percona: <c>mariadb-dump</c> or <c>mysqldump</c> in one transaction.</summary>
public static class MySqlDatabase
{
    public static ShellDatabase Definition { get; } = new(
        [DatabaseKind.MySql, DatabaseKind.MariaDb],
        "sql",
        Credentials:
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
        Dump:
            """
            DUMP="$(command -v mariadb-dump || command -v mysqldump)"
            exec "$DUMP" -u"$DB_USER" $SCOPE --single-transaction --routines --events --triggers
            """,
        Probe:
            """
            CLIENT="$(command -v mariadb || command -v mysql)"
            exec "$CLIENT" -u"$DB_USER" -N -e 'select 1'
            """,
        Drill: file => new DrillCheck(
            [$"MYSQL_ROOT_PASSWORD={DrillChecks.Password}", $"MARIADB_ROOT_PASSWORD={DrillChecks.Password}"],
            null,
            ["sh", "-c", $"$(command -v mariadb-admin || command -v mysqladmin) ping -h 127.0.0.1 -uroot -p{DrillChecks.Password} --silent"],
            ["sh", "-c", $"$(command -v mariadb || command -v mysql) -h 127.0.0.1 -uroot -p{DrillChecks.Password} < {file}"],
            ["sh", "-c", $"$(command -v mariadb || command -v mysql) -h 127.0.0.1 -uroot -p{DrillChecks.Password} -N -e \"select count(*) from information_schema.schemata where schema_name not in ('mysql','information_schema','performance_schema','sys')\""],
            "databases"));
}
