using Admin.Service.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HsSqlAgent.PostgresMigrations.Admin;

/// <summary>
/// Database-enforced v3 upgrade marker. Do not allow a direct EF migration
/// to silently cross the approval protocol boundary with live v2 requests.
/// </summary>
[DbContext(typeof(AdminContext))]
[Migration("20261022190000_V3ApprovalDrainGatePostgres")]
public sealed class V3ApprovalDrainGatePostgres : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $v3drain$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM "DmlApprovalRequests"
                    WHERE "Status" IN ('Pending', 'Executing')
                ) THEN
                    RAISE EXCEPTION 'HsSqlAgent v3 upgrade requires draining Pending/Executing DML approvals and old workers before migration';
                END IF;
            END $v3drain$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Do not mutate persisted approvals when removing only the marker.
    }
}
