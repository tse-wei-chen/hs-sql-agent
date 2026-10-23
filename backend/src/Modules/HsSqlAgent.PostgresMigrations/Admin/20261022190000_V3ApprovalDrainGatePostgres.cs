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

        migrationBuilder.Sql("""
            ALTER TABLE "DmlApprovalRequests"
            ADD COLUMN "AdmissionProtocolVersion" integer NOT NULL DEFAULT 0;
            ALTER TABLE "DmlApprovalRequests"
            ADD CONSTRAINT "V3DurableDmlAdmissionProtocolRequired"
                CHECK (
                    "Status" NOT IN ('Pending', 'Executing')
                    OR "AdmissionProtocolVersion" >= 1
                );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Neither silently drop the history marker while leaving the CHECK,
        // nor drop the CHECK and accidentally re-admit legacy Pending rows.
        throw new NotSupportedException(
            "HsSqlAgent v3 DML approval admission is a one-way security migration. " +
            "Do not roll this migration back; restore a coordinated backup with " +
            "all DML workers stopped and reconcile outstanding approvals.");
    }
}
