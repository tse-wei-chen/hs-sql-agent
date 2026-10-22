using Admin.Service.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HsSqlAgent.SqliteMigrations.Admin;

/// <summary>
/// v3 protocol admission marker. The CHECK constraint turns any outstanding
/// v2 approval into a failed migration, even when EF migration is invoked
/// directly (without the application startup preflight).
/// </summary>
[DbContext(typeof(AdminContext))]
[Migration("20261022190000_V3ApprovalDrainGate")]
public sealed class V3ApprovalDrainGate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TEMP TABLE "__HsSqlAgentV3DrainGate" (
                "OpenApprovalCount" INTEGER NOT NULL
                    CONSTRAINT "V3UpgradeRequiresDrainedDmlApprovals"
                    CHECK ("OpenApprovalCount" = 0)
            );
            INSERT INTO "__HsSqlAgentV3DrainGate" ("OpenApprovalCount")
            SELECT COUNT(*) FROM "DmlApprovalRequests"
            WHERE "Status" IN ('Pending', 'Executing');
            DROP TABLE "__HsSqlAgentV3DrainGate";
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // A rollback of an empty protocol marker must never reopen approvals.
    }
}
