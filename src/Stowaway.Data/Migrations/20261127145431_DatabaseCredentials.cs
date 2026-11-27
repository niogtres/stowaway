using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stowaway.Data.Migrations
{
    /// <inheritdoc />
    public partial class DatabaseCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DatabaseCredentials",
                columns: table => new
                {
                    ContainerName = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    AppId = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    User = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    PasswordProtected = table.Column<string>(type: "TEXT", nullable: false),
                    Database = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatabaseCredentials", x => x.ContainerName);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DatabaseCredentials");
        }
    }
}
