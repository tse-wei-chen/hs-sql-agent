using Admin.Service.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HsSqlAgent.SqliteMigrations.Admin;

/// <summary>
/// Marks Admin Stores created under the breaking v3-only security contract.
/// Initialization must reject an unmarked pre-existing store BEFORE migrating.
/// </summary>
[DbContext(typeof(AdminContext))]
[Migration("20261024115500_V3FreshStore")]
public sealed class V3FreshStore : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) { }
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("v3 Admin Stores cannot be downgraded to v2.");
}
