using Stowaway.Core.Inventory;

namespace Stowaway.Tests.Inventory;

public sealed class DatabaseDetectorTests
{
    [Theory]
    [InlineData("postgres:17", DatabaseKind.PostgreSql)]
    [InlineData("postgis/postgis:16-3.4", DatabaseKind.PostgreSql)]
    [InlineData("timescale/timescaledb:latest-pg16", DatabaseKind.PostgreSql)]
    [InlineData("bitnami/postgresql", DatabaseKind.PostgreSql)]
    [InlineData("mysql:8.0", DatabaseKind.MySql)]
    [InlineData("mariadb:11", DatabaseKind.MariaDb)]
    [InlineData("lscr.io/linuxserver/mariadb:latest", DatabaseKind.MariaDb)]
    [InlineData("mongo:7", DatabaseKind.MongoDb)]
    [InlineData("redis:7-alpine", DatabaseKind.Redis)]
    [InlineData("valkey/valkey:8", DatabaseKind.Redis)]
    [InlineData("mcr.microsoft.com/mssql/server:2022-latest", DatabaseKind.SqlServer)]
    [InlineData("docker.elastic.co/elasticsearch/elasticsearch:8.15.0", DatabaseKind.Elasticsearch)]
    [InlineData("influxdb:2", DatabaseKind.InfluxDb)]
    [InlineData("localhost:5000/team/postgres:17@sha256:abc", DatabaseKind.PostgreSql)]
    public void Recognises_database_images(string image, DatabaseKind expected) =>
        Assert.Equal(expected, DatabaseDetector.Detect(image));

    [Theory]
    [InlineData("phpmyadmin:latest")]
    [InlineData("adminer")]
    [InlineData("dpage/pgadmin4")]
    [InlineData("prometheuscommunity/postgres-exporter")]
    [InlineData("edoburu/pgbouncer")]
    [InlineData("linuxserver/jellyfin:latest")]
    [InlineData("nginx")]
    public void Ignores_tools_and_other_images(string image) =>
        Assert.Null(DatabaseDetector.Detect(image));

    [Theory]
    // Defaults and wrapped server starts: still databases.
    [InlineData("postgres:17", "docker-entrypoint.sh postgres", true)]
    [InlineData("postgres:17", "/bin/sh -c 'docker-entrypoint.sh postgres -c shared_buffers=256MB'", true)]
    [InlineData("postgres:17", "sh -c \"exec postgres -c max_connections=200\"", true)]
    [InlineData("mysql:8.0", "docker-entrypoint.sh --log-bin=mysql-bin --server-id=1", true)]
    [InlineData("mysql:8.0", "bash -c 'mysqld --skip-name-resolve'", true)]
    [InlineData("redis:7", "redis-server --appendonly yes", true)]
    [InlineData("postgres:17", null, true)]
    // A backup sidecar on the postgres image: a client.
    [InlineData("postgres:17", "/bin/sh -c '# wait for the server\nuntil psql -tAc \"select 1\" -U postgres; do sleep 15; done\nwhile true; do pg_dump -Fc -f /backups/daily.dump; sleep 86400; done'", false)]
    [InlineData("mysql:8.0", "sh -c 'while true; do mysqldump -h db --all-databases > /backup/all.sql; sleep 3600; done'", false)]
    [InlineData("redis:7", "sh -c 'redis-cli -h cache monitor'", false)]
    public void Database_images_running_client_scripts_are_not_servers(string image, string? command, bool server) =>
        Assert.Equal(server, DatabaseDetector.Detect(image, command) is not null);
}
