using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stowaway.Data.Migrations
{
    /// <inheritdoc />
    public partial class RecoveryKeyConfirmed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "RecoveryKeyConfirmedAt",
                table: "Destinations",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RecoveryKeyConfirmedAt",
                table: "Destinations");
        }
    }
}
