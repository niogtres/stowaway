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
}
