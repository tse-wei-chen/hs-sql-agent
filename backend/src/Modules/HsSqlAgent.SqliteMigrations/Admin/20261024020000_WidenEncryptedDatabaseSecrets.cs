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
        // SQLite stored PasswordHash as TEXT already. SQLite has no
        // varchar(256) storage limit; the old bound existed only in the EF
        // model metadata. An AlterColumn here raises NotSupportedException
        // in SQLite's migrations SQL generator. Snapshot changes capture the
        // new unbounded model; no physical table rewrite is necessary.
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException(
            "A schema downgrade is unsafe after v2 encrypted credentials are written; restore a coordinated backup.");
}
