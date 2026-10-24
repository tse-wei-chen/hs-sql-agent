using Admin.Service.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HsSqlAgent.SqliteMigrations.Admin;

/// <summary>
/// SQLite TEXT is physically unbounded; remove the 256-character EF model
/// facet to match the PostgreSQL versioned-envelope schema contract.
/// </summary>
[DbContext(typeof(AdminContext))]
[Migration("20261024020000_WidenEncryptedDatabaseSecrets")]
public sealed class WidenEncryptedDatabaseSecrets : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "PasswordHash",
            table: "DbManagement",
            type: "TEXT",
            nullable: true,
            oldClrType: typeof(string),
            oldType: "TEXT",
            oldMaxLength: 256,
            oldNullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException(
            "A schema downgrade is unsafe after v2 encrypted credentials are written; restore a coordinated backup.");
}
