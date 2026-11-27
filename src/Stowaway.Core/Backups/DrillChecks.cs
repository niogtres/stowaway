using Stowaway.Core.Inventory;

namespace Stowaway.Core.Backups;

/// <summary>How to start a throwaway database from a dump and prove the data is there.</summary>
/// <param name="Environment">Extra environment for the throwaway container.</param>
/// <param name="CommandOverride">Replaces the image's command (e.g. Redis loads its dump at start-up).</param>
/// <param name="Ready">Succeeds once the server accepts real (TCP) connections, not the init-time server.</param>
/// <param name="Load">Loads the dump; null when the dump is loaded at start-up.</param>
/// <param name="Probe">Prints a single number describing the restored data.</param>
/// <param name="ProbeLabel">What the probe's number counts.</param>
public sealed record DrillCheck(
    IReadOnlyList<string> Environment,
    IReadOnlyList<string>? CommandOverride,
    IReadOnlyList<string> Ready,
    IReadOnlyList<string>? Load,
    IReadOnlyList<string> Probe,
    string ProbeLabel);

public static class DrillChecks
{
    public const string DumpDir = "/drill";
    public const string Password = "drill-only";

    public static DrillCheck? For(DatabaseKind kind, string dumpFile)
    {
        var file = $"{DumpDir}/{dumpFile}";
        return kind switch
        {
            DatabaseKind.PostgreSql => new DrillCheck(
                [$"POSTGRES_PASSWORD={Password}"],
                null,
                ["pg_isready", "-h", "127.0.0.1", "-U", "postgres"],
                ["sh", "-c", $"psql -U postgres -q -f {file} >/dev/null 2>/tmp/load.err; echo ok"],
                ["psql", "-U", "postgres", "-Atc", "select count(*) from pg_database where not datistemplate and datname <> 'postgres'"],
                "databases"),

            DatabaseKind.MySql or DatabaseKind.MariaDb => new DrillCheck(
                [$"MYSQL_ROOT_PASSWORD={Password}", $"MARIADB_ROOT_PASSWORD={Password}"],
                null,
                ["sh", "-c", $"$(command -v mariadb-admin || command -v mysqladmin) ping -h 127.0.0.1 -uroot -p{Password} --silent"],
                ["sh", "-c", $"$(command -v mariadb || command -v mysql) -h 127.0.0.1 -uroot -p{Password} < {file}"],
                ["sh", "-c", $"$(command -v mariadb || command -v mysql) -h 127.0.0.1 -uroot -p{Password} -N -e \"select count(*) from information_schema.schemata where schema_name not in ('mysql','information_schema','performance_schema','sys')\""],
                "databases"),

            DatabaseKind.MongoDb => new DrillCheck(
                [],
                null,
                ["mongosh", "--quiet", "--eval", "db.runCommand({ping:1}).ok"],
                ["mongorestore", $"--archive={file}", "--quiet"],
                ["mongosh", "--quiet", "--eval", "db.adminCommand('listDatabases').databases.filter(d => !['admin','config','local'].includes(d.name)).length"],
                "databases"),

            DatabaseKind.Redis => new DrillCheck(
                [],
                ["sh", "-c", $"mkdir -p /data && cp {file} /data/dump.rdb && exec redis-server --dir /data --dbfilename dump.rdb"],
                ["redis-cli", "ping"],
                null,
                ["redis-cli", "dbsize"],
                "keys"),

            _ => null,
        };
    }
}
