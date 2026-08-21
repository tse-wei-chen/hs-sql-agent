using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace HsSqlAgent.SqliteMigrations.Auth
{
    /// <inheritdoc />
    public partial class AddP1GovernancePermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "PermissionActionTemplates",
                columns: new[] { "Id", "ActionId", "PermissionId" },
                values: new object[] { 26, 3, 2 });

            migrationBuilder.InsertData(
                table: "Permissions",
                columns: new[] { "Id", "Name", "Path" },
                values: new object[] { 9, "Security Policy", "/runtime/security" });

            migrationBuilder.InsertData(
                table: "PermissionActionTemplates",
                columns: new[] { "Id", "ActionId", "PermissionId" },
                values: new object[,]
                {
                    { 24, 1, 9 },
                    { 25, 3, 9 }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "PermissionActionTemplates",
                keyColumn: "Id",
                keyValue: 24);

            migrationBuilder.DeleteData(
                table: "PermissionActionTemplates",
                keyColumn: "Id",
                keyValue: 25);

            migrationBuilder.DeleteData(
                table: "PermissionActionTemplates",
                keyColumn: "Id",
                keyValue: 26);

            migrationBuilder.DeleteData(
                table: "Permissions",
                keyColumn: "Id",
                keyValue: 9);
        }
    }
}
