using System;
using Admin.Service.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HsSqlAgent.SqliteMigrations.Admin;

[DbContext(typeof(AdminContext))]
[Migration("20261018150000_AddManagementApiKeys")]
public sealed class AddManagementApiKeys : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(name: "ManagementApiKeys", columns: table => new
        {
            Id = table.Column<int>(type: "INTEGER", nullable: false) .Annotation("Sqlite:Autoincrement", true),
            Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
            KeyPrefix = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
            KeyHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
            PermissionsJson = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
            CreatedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
            CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
            ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
            LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
            RevokedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
            RevokedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true)
        }, constraints: table => table.PrimaryKey("PK_ManagementApiKeys", x => x.Id));
        migrationBuilder.CreateIndex("IX_ManagementApiKeys_KeyPrefix", "ManagementApiKeys", "KeyPrefix");
        migrationBuilder.CreateIndex("IX_ManagementApiKeys_KeyHash", "ManagementApiKeys", "KeyHash", unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropTable("ManagementApiKeys");
}
