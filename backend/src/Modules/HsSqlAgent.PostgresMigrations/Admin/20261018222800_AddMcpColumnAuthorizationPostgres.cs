using Admin.Service.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HsSqlAgent.PostgresMigrations.Admin;

[DbContext(typeof(AdminContext))]
[Migration("20261018222800_AddMcpColumnAuthorizationPostgres")]
public sealed class AddMcpColumnAuthorizationPostgres : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.AddColumn<string>(
            name: "AllowedQueryColumns", table: "McpAccessKeys",
            type: "character varying(4000)", maxLength: 4000, nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropColumn(name: "AllowedQueryColumns", table: "McpAccessKeys");
}
