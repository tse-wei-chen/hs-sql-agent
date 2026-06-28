using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auth.Service.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTokenBlacklist : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TokenBlacklistEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Jti = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TokenBlacklistEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TokenBlacklistEntries_ExpiresAt",
                table: "TokenBlacklistEntries",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_TokenBlacklistEntries_Jti",
                table: "TokenBlacklistEntries",
                column: "Jti",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TokenBlacklistEntries");
        }
    }
}
