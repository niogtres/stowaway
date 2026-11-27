using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stowaway.Data.Migrations
{
    /// <inheritdoc />
    public partial class PlansAndNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "BackupRuns",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Backup"); // existing rows are all backups

            migrationBuilder.AddColumn<Guid>(
                name: "PlanId",
                table: "BackupRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SnapshotsForgotten",
                table: "BackupRuns",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "NotificationChannels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    OnFailure = table.Column<bool>(type: "INTEGER", nullable: false),
                    OnRecovery = table.Column<bool>(type: "INTEGER", nullable: false),
                    OnSuccess = table.Column<bool>(type: "INTEGER", nullable: false),
                    ProtectedSettings = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationChannels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Plans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Selection = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    AppIds = table.Column<string>(type: "TEXT", nullable: false),
                    ExcludedAppIds = table.Column<string>(type: "TEXT", nullable: false),
                    IncludeOrphanedVolumes = table.Column<bool>(type: "INTEGER", nullable: false),
                    Strategy = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    DestinationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Cron = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastTriggeredAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Retention = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Plans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Plans_Destinations_DestinationId",
                        column: x => x.DestinationId,
                        principalTable: "Destinations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BackupRuns_PlanId",
                table: "BackupRuns",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationChannels_Name",
                table: "NotificationChannels",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Plans_DestinationId",
                table: "Plans",
                column: "DestinationId");

            migrationBuilder.CreateIndex(
                name: "IX_Plans_Name",
                table: "Plans",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationChannels");

            migrationBuilder.DropTable(
                name: "Plans");

            migrationBuilder.DropIndex(
                name: "IX_BackupRuns_PlanId",
                table: "BackupRuns");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "BackupRuns");

            migrationBuilder.DropColumn(
                name: "PlanId",
                table: "BackupRuns");

            migrationBuilder.DropColumn(
                name: "SnapshotsForgotten",
                table: "BackupRuns");
        }
    }
}
