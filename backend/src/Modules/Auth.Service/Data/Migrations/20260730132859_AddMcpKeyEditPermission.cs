using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auth.Service.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpKeyEditPermission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "PermissionActionTemplates",
                columns: new[] { "Id", "ActionId", "PermissionId" },
                values: new object[] { 26, 3, 2 });

            migrationBuilder.Sql(
                """
                INSERT OR IGNORE INTO PermissionActions (RoleId, PermissionId, ActionId)
                SELECT r.Id, pat.PermissionId, pat.ActionId
                FROM Roles r
                CROSS JOIN PermissionActionTemplates pat
                WHERE r.Name = 'SuperUser'
                  AND pat.Id IN (24, 25, 26);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DELETE FROM PermissionActions
                WHERE RoleId IN (SELECT Id FROM Roles WHERE Name = 'SuperUser')
                  AND (
                    (PermissionId = 9 AND ActionId IN (1, 3))
                    OR (PermissionId = 2 AND ActionId = 3)
                  );
                """);

            migrationBuilder.DeleteData(
                table: "PermissionActionTemplates",
                keyColumn: "Id",
                keyValue: 26);
        }
    }
}
