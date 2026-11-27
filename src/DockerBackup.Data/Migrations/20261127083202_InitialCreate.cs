using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DockerBackup.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Destinations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    SmbUsername = table.Column<string>(type: "TEXT", nullable: true),
                    SmbDomain = table.Column<string>(type: "TEXT", nullable: true),
                    ProtectedSmbPassword = table.Column<string>(type: "TEXT", nullable: true),
                    ProtectedRepositoryPassword = table.Column<string>(type: "TEXT", nullable: false),
                    RepositoryId = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Destinations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BackupRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppId = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    AppName = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    DestinationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Strategy = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Trigger = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    QueuedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    FinishedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    SnapshotId = table.Column<string>(type: "TEXT", nullable: true),
                    FilesProcessed = table.Column<long>(type: "INTEGER", nullable: true),
                    BytesProcessed = table.Column<long>(type: "INTEGER", nullable: true),
                    BytesAdded = table.Column<long>(type: "INTEGER", nullable: true),
                    DowntimeSeconds = table.Column<double>(type: "REAL", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    Log = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BackupRuns_Destinations_DestinationId",
                        column: x => x.DestinationId,
                        principalTable: "Destinations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BackupRuns_AppId_QueuedAt",
                table: "BackupRuns",
                columns: new[] { "AppId", "QueuedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BackupRuns_DestinationId",
                table: "BackupRuns",
                column: "DestinationId");

            migrationBuilder.CreateIndex(
                name: "IX_BackupRuns_QueuedAt",
                table: "BackupRuns",
                column: "QueuedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Destinations_Name",
                table: "Destinations",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BackupRuns");

            migrationBuilder.DropTable(
                name: "Destinations");
        }
    }
}
