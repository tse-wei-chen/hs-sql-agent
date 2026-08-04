using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Auth.Service.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOperabilityPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "AuthActions",
                columns: new[] { "Id", "Code", "Name" },
                values: new object[] { 6, "export", "export" });

            migrationBuilder.InsertData(
                table: "PermissionActionTemplates",
                columns: new[] { "Id", "ActionId", "PermissionId" },
                values: new object[] { 27, 3, 5 });

            migrationBuilder.InsertData(
                table: "Permissions",
                columns: new[] { "Id", "Name", "Path" },
                values: new object[] { 10, "Operability", "/runtime/operability" });

            migrationBuilder.InsertData(
                table: "PermissionActionTemplates",
                columns: new[] { "Id", "ActionId", "PermissionId" },
                values: new object[,]
                {
                    { 28, 6, 5 },
                    { 29, 1, 10 },
                    { 30, 3, 10 }
                });

            migrationBuilder.Sql("""
                INSERT OR IGNORE INTO PermissionActions (RoleId, PermissionId, ActionId)
                SELECT r.Id, pat.PermissionId, pat.ActionId
                FROM Roles r
                CROSS JOIN PermissionActionTemplates pat
                WHERE r.Name = 'SuperUser' AND pat.Id IN (27, 28, 29, 30);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM PermissionActions
                WHERE RoleId IN (SELECT Id FROM Roles WHERE Name = 'SuperUser')
                  AND ((PermissionId = 5 AND ActionId IN (3, 6)) OR PermissionId = 10);
                """);
            migrationBuilder.DeleteData(
                table: "PermissionActionTemplates",
                keyColumn: "Id",
                keyValue: 27);

            migrationBuilder.DeleteData(
                table: "PermissionActionTemplates",
                keyColumn: "Id",
                keyValue: 28);

            migrationBuilder.DeleteData(
                table: "PermissionActionTemplates",
                keyColumn: "Id",
                keyValue: 29);

            migrationBuilder.DeleteData(
                table: "PermissionActionTemplates",
                keyColumn: "Id",
                keyValue: 30);

            migrationBuilder.DeleteData(
                table: "AuthActions",
                keyColumn: "Id",
                keyValue: 6);

            migrationBuilder.DeleteData(
                table: "Permissions",
                keyColumn: "Id",
                keyValue: 10);
        }
    }
}
