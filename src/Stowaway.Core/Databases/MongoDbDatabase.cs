using Stowaway.Core.Backups;
using Stowaway.Core.Inventory;

namespace Stowaway.Core.Databases;

/// <summary>MongoDB: <c>mongodump --archive</c>.</summary>
public static class MongoDbDatabase
{
    public static ShellDatabase Definition { get; } = new(
        [DatabaseKind.MongoDb],
        "archive",
        Credentials:
            """
            f() { [ -n "$1" ] && cat "$1" 2>/dev/null; }
            DB_USER="${STOWAWAY_DB_USER:-${MONGO_INITDB_ROOT_USERNAME:-$(f "$MONGO_INITDB_ROOT_USERNAME_FILE")}}"
            DB_PASS="${STOWAWAY_DB_PASSWORD:-${MONGO_INITDB_ROOT_PASSWORD:-$(f "$MONGO_INITDB_ROOT_PASSWORD_FILE")}}"
            """,
        Dump:
            """
            if [ -z "$DB_USER" ]; then exec mongodump --archive --quiet; fi
            if [ -n "$STOWAWAY_DB_NAME" ]; then
              exec mongodump --archive --quiet --username "$DB_USER" --password "$DB_PASS" --authenticationDatabase admin --db "$STOWAWAY_DB_NAME"
            fi
            exec mongodump --archive --quiet --username "$DB_USER" --password "$DB_PASS" --authenticationDatabase admin
            """,
        Probe:
            """
            SHELL_BIN="$(command -v mongosh || command -v mongo)"
            if [ -z "$DB_USER" ]; then exec "$SHELL_BIN" --quiet --eval 'db.runCommand({ ping: 1 }).ok'; fi
            exec "$SHELL_BIN" --quiet --username "$DB_USER" --password "$DB_PASS" --authenticationDatabase admin --eval 'db.runCommand({ ping: 1 }).ok'
            """,
        Drill: file => new DrillCheck(
            [],
            null,
            ["mongosh", "--quiet", "--eval", "db.runCommand({ping:1}).ok"],
            ["mongorestore", $"--archive={file}", "--quiet"],
            ["mongosh", "--quiet", "--eval", "db.adminCommand('listDatabases').databases.filter(d => !['admin','config','local'].includes(d.name)).length"],
            "databases"));
}
