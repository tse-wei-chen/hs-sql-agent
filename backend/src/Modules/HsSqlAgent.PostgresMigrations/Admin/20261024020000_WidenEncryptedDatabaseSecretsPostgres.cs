using Admin.Service.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HsSqlAgent.PostgresMigrations.Admin;

/// <summary>
/// Old Admin Store constrained encrypted passwords to varchar(256). v2 AES-GCM
/// prefixes, key IDs and authentication tags can exceed that bound.
/// </summary>
[DbContext(typeof(AdminContext))]
[Migration("20261024020000_WidenEncryptedDatabaseSecretsPostgres")]
public sealed class WidenEncryptedDatabaseSecretsPostgres : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "PasswordHash",
            table: "DbManagement",
            type: "text",
            nullable: true,
            oldClrType: typeof(string),
            oldType: "character varying(256)",
            oldMaxLength: 256,
            oldNullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException(
            "Versioned database credentials can exceed 256 characters. " +
            "Schema downgrade requires a separate, verified credential inventory and coordinated restore.");
}
