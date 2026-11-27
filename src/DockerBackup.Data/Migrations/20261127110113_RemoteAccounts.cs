using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DockerBackup.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoteAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RemoteAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, collation: "NOCASE"),
                    PasswordHash = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    TotpSecretProtected = table.Column<string>(type: "TEXT", nullable: false),
                    TotpConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastTotpStep = table.Column<long>(type: "INTEGER", nullable: false),
                    RecoveryCodeHashes = table.Column<string>(type: "TEXT", nullable: false),
                    FailedAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LockedUntil = table.Column<long>(type: "INTEGER", nullable: true),
                    SecurityStamp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    LastSignInAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteAccounts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RemoteAccounts_UserName",
                table: "RemoteAccounts",
                column: "UserName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RemoteAccounts");
        }
    }
}
