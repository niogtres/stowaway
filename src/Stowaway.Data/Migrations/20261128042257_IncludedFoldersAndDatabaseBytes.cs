using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stowaway.Data.Migrations
{
    /// <inheritdoc />
    public partial class IncludedFoldersAndDatabaseBytes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "DatabaseBytes",
                table: "BackupRuns",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "IncludedFolders",
                columns: table => new
                {
                    Path = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false, collation: "NOCASE"),
                    AddedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncludedFolders", x => x.Path);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IncludedFolders");

            migrationBuilder.DropColumn(
                name: "DatabaseBytes",
                table: "BackupRuns");
        }
    }
}
