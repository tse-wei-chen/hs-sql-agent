using System;
using Admin.Service.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HsSqlAgent.PostgresMigrations.Admin;

[DbContext(typeof(AdminContext))]
[Migration("20261018150000_AddManagementApiKeysPostgres")]
public sealed class AddManagementApiKeysPostgres : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(name: "ManagementApiKeys", columns: table => new
        {
            Id = table.Column<int>(type: "integer", nullable: false).Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
            Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
            KeyPrefix = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
            KeyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
            PermissionsJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
            CreatedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
            CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
            ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
            LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
            RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
            RevokedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
        }, constraints: table => table.PrimaryKey("PK_ManagementApiKeys", x => x.Id));
        migrationBuilder.CreateIndex("IX_ManagementApiKeys_KeyPrefix", "ManagementApiKeys", "KeyPrefix");
        migrationBuilder.CreateIndex("IX_ManagementApiKeys_KeyHash", "ManagementApiKeys", "KeyHash", unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropTable("ManagementApiKeys");
}
