using Admin.Service.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HsSqlAgent.SqliteMigrations.Admin
{
    /// <inheritdoc />
    [DbContext(typeof(AdminContext))]
    [Migration("20260627034600_MigrateSuperUsersToAuth")]
    public partial class MigrateSuperUsersToAuth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT OR IGNORE INTO Roles (Name, Description)
                VALUES ('SuperUser', 'Built-in role with unrestricted administrative access.');
                """);

            migrationBuilder.Sql("""
                INSERT OR IGNORE INTO Members (Id, Username, Mail, PasswordHash)
                SELECT Id, Username, Mail, PasswordHash
                FROM SuperUsers;
                """);

            migrationBuilder.Sql("""
                INSERT OR IGNORE INTO MemberRoles (MemberId, RoleId)
                SELECT m.Id, r.Id
                FROM SuperUsers su
                INNER JOIN Members m ON m.Mail = su.Mail
                INNER JOIN Roles r ON r.Name = 'SuperUser';
                """);

            migrationBuilder.Sql("""
                INSERT OR IGNORE INTO PermissionActions (RoleId, PermissionId, ActionId)
                SELECT r.Id, pat.PermissionId, pat.ActionId
                FROM Roles r
                CROSS JOIN PermissionActionTemplates pat
                WHERE r.Name = 'SuperUser';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM MemberRoles
                WHERE RoleId IN (
                    SELECT Id
                    FROM Roles
                    WHERE Name = 'SuperUser'
                );
                """);

            migrationBuilder.Sql("""
                DELETE FROM Members
                WHERE Mail IN (
                    SELECT Mail
                    FROM SuperUsers
                );
                """);
        }
    }
}
