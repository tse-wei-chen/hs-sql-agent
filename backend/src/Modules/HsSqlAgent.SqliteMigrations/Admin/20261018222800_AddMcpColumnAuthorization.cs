using Admin.Service.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HsSqlAgent.SqliteMigrations.Admin;

[DbContext(typeof(AdminContext))]
[Migration("20261018222800_AddMcpColumnAuthorization")]
public sealed class AddMcpColumnAuthorization : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.AddColumn<string>(
            name: "AllowedQueryColumns", table: "McpAccessKeys",
            type: "TEXT", maxLength: 4000, nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropColumn(name: "AllowedQueryColumns", table: "McpAccessKeys");
}
