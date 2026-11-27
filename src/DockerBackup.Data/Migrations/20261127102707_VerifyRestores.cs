using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DockerBackup.Data.Migrations
{
    /// <inheritdoc />
    public partial class VerifyRestores : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "VerifyRestores",
                table: "Plans",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VerifyRestores",
                table: "Plans");
        }
    }
}
