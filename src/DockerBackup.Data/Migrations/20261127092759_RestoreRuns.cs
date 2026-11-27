using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DockerBackup.Data.Migrations
{
    /// <inheritdoc />
    public partial class RestoreRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Details",
                table: "BackupRuns",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Details",
                table: "BackupRuns");
        }
    }
}
