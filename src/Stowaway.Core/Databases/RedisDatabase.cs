using Stowaway.Core.Backups;
using Stowaway.Core.Inventory;

namespace Stowaway.Core.Databases;

/// <summary>Redis, Valkey and KeyDB: an RDB snapshot through <c>redis-cli --rdb</c>.</summary>
public static class RedisDatabase
{
    public static ShellDatabase Definition { get; } = new(
        [DatabaseKind.Redis],
        "rdb",
        Credentials:
            """
            f() { [ -n "$1" ] && cat "$1" 2>/dev/null; }
            DB_USER="$STOWAWAY_DB_USER"
            DB_PASS="${STOWAWAY_DB_PASSWORD:-${REDIS_PASSWORD:-$(f "$REDIS_PASSWORD_FILE")}}"
            """,
        Dump:
            """exec redis-cli ${DB_USER:+--user "$DB_USER"} ${DB_PASS:+-a "$DB_PASS"} --no-auth-warning --rdb -""",
        Probe:
            """exec redis-cli ${DB_USER:+--user "$DB_USER"} ${DB_PASS:+-a "$DB_PASS"} --no-auth-warning ping""",
        Drill: file => new DrillCheck(
            [],
            ["sh", "-c", $"mkdir -p /data && cp {file} /data/dump.rdb && exec redis-server --dir /data --dbfilename dump.rdb"],
            ["redis-cli", "ping"],
            null,
            ["redis-cli", "dbsize"],
            "keys"));
}
