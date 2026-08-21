using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace HsSqlAgent.SqliteMigrations.Auth
{
    /// <inheritdoc />
    public partial class InitAuthContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuthActions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Code = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthActions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Members",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Username = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Mail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Members", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Permissions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 250, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Permissions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Roles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PermissionActionTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PermissionId = table.Column<int>(type: "INTEGER", nullable: false),
                    ActionId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermissionActionTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PermissionActionTemplates_AuthActions_ActionId",
                        column: x => x.ActionId,
                        principalTable: "AuthActions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PermissionActionTemplates_Permissions_PermissionId",
                        column: x => x.PermissionId,
                        principalTable: "Permissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MemberRoles",
                columns: table => new
                {
                    MemberId = table.Column<int>(type: "INTEGER", nullable: false),
                    RoleId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemberRoles", x => new { x.MemberId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_MemberRoles_Members_MemberId",
                        column: x => x.MemberId,
                        principalTable: "Members",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MemberRoles_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PermissionActions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RoleId = table.Column<int>(type: "INTEGER", nullable: false),
                    PermissionId = table.Column<int>(type: "INTEGER", nullable: false),
                    ActionId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermissionActions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PermissionActions_AuthActions_ActionId",
                        column: x => x.ActionId,
                        principalTable: "AuthActions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PermissionActions_Permissions_PermissionId",
                        column: x => x.PermissionId,
                        principalTable: "Permissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PermissionActions_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "AuthActions",
                columns: new[] { "Id", "Code", "Name" },
                values: new object[,]
                {
                    { 1, "view", "view" },
                    { 2, "create", "create" },
                    { 3, "edit", "edit" },
                    { 4, "delete", "delete" },
                    { 5, "revoke", "revoke" }
                });

            migrationBuilder.InsertData(
                table: "Permissions",
                columns: new[] { "Id", "Name", "Path" },
                values: new object[,]
                {
                    { 1, "Overview", "/home" },
                    { 2, "MCP Keys", "/runtime/mcp-keys" },
                    { 3, "Custom Tools", "/runtime/custom-tools" },
                    { 4, "DB Management", "/runtime/db-management" },
                    { 5, "Audit", "/runtime/audit" },
                    { 6, "Role Management", "/auth/role" },
                    { 7, "User Management", "/auth/user" },
                    { 8, "Semantic Layer", "/runtime/db-management/semantic" }
                });

            migrationBuilder.InsertData(
                table: "PermissionActionTemplates",
                columns: new[] { "Id", "ActionId", "PermissionId" },
                values: new object[,]
                {
                    { 1, 1, 1 },
                    { 2, 1, 2 },
                    { 3, 2, 2 },
                    { 4, 5, 2 },
                    { 5, 1, 3 },
                    { 6, 2, 3 },
                    { 7, 3, 3 },
                    { 8, 4, 3 },
                    { 9, 1, 4 },
                    { 10, 2, 4 },
                    { 11, 3, 4 },
                    { 12, 4, 4 },
                    { 13, 1, 5 },
                    { 14, 1, 6 },
                    { 15, 2, 6 },
                    { 16, 3, 6 },
                    { 17, 4, 6 },
                    { 18, 1, 7 },
                    { 19, 2, 7 },
                    { 20, 3, 7 },
                    { 21, 4, 7 },
                    { 22, 1, 8 },
                    { 23, 3, 8 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuthActions_Code",
                table: "AuthActions",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MemberRoles_RoleId",
                table: "MemberRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_Members_Mail",
                table: "Members",
                column: "Mail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PermissionActions_ActionId",
                table: "PermissionActions",
                column: "ActionId");

            migrationBuilder.CreateIndex(
                name: "IX_PermissionActions_PermissionId",
                table: "PermissionActions",
                column: "PermissionId");

            migrationBuilder.CreateIndex(
                name: "IX_PermissionActions_RoleId_PermissionId_ActionId",
                table: "PermissionActions",
                columns: new[] { "RoleId", "PermissionId", "ActionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PermissionActionTemplates_ActionId",
                table: "PermissionActionTemplates",
                column: "ActionId");

            migrationBuilder.CreateIndex(
                name: "IX_PermissionActionTemplates_PermissionId_ActionId",
                table: "PermissionActionTemplates",
                columns: new[] { "PermissionId", "ActionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Permissions_Path",
                table: "Permissions",
                column: "Path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Roles_Name",
                table: "Roles",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MemberRoles");

            migrationBuilder.DropTable(
                name: "PermissionActions");

            migrationBuilder.DropTable(
                name: "PermissionActionTemplates");

            migrationBuilder.DropTable(
                name: "Members");

            migrationBuilder.DropTable(
                name: "Roles");

            migrationBuilder.DropTable(
                name: "AuthActions");

            migrationBuilder.DropTable(
                name: "Permissions");
        }
    }
}
