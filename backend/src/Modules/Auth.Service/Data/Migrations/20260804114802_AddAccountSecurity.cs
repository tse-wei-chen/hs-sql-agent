using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auth.Service.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Members_Mail",
                table: "Members");

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "Members",
                type: "TEXT",
                nullable: false,
                // SQLite rejects ALTER TABLE ADD COLUMN when the default is a
                // non-constant expression such as CURRENT_TIMESTAMP and the
                // table already contains rows. Use a constant only to make the
                // schema change legal; the UPDATE below backfills every legacy
                // member with the actual migration time.
                defaultValue: new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            migrationBuilder.AddColumn<int>(
                name: "FailedSignInCount",
                table: "Members",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastLoginAt",
                table: "Members",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LockoutEnd",
                table: "Members",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedMail",
                table: "Members",
                type: "TEXT",
                maxLength: 320,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "RequirePasswordChangeAtNextSignIn",
                table: "Members",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("UPDATE Members SET NormalizedMail = upper(trim(Mail)), CreatedAt = CURRENT_TIMESTAMP;");

            migrationBuilder.CreateTable(
                name: "PasswordResetTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MemberId = table.Column<int>(type: "INTEGER", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UsedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PasswordResetTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PasswordResetTokens_Members_MemberId",
                        column: x => x.MemberId,
                        principalTable: "Members",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Members_NormalizedMail",
                table: "Members",
                column: "NormalizedMail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PasswordResetTokens_ExpiresAt",
                table: "PasswordResetTokens",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_PasswordResetTokens_MemberId",
                table: "PasswordResetTokens",
                column: "MemberId");

            migrationBuilder.CreateIndex(
                name: "IX_PasswordResetTokens_TokenHash",
                table: "PasswordResetTokens",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PasswordResetTokens");

            migrationBuilder.DropIndex(
                name: "IX_Members_NormalizedMail",
                table: "Members");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "Members");

            migrationBuilder.DropColumn(
                name: "FailedSignInCount",
                table: "Members");

            migrationBuilder.DropColumn(
                name: "LastLoginAt",
                table: "Members");

            migrationBuilder.DropColumn(
                name: "LockoutEnd",
                table: "Members");

            migrationBuilder.DropColumn(
                name: "NormalizedMail",
                table: "Members");

            migrationBuilder.DropColumn(
                name: "RequirePasswordChangeAtNextSignIn",
                table: "Members");

            migrationBuilder.CreateIndex(
                name: "IX_Members_Mail",
                table: "Members",
                column: "Mail",
                unique: true);
        }
    }
}
