using Admin.Service.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace HsSqlAgent.SqliteMigrations.Admin;

[DbContext(typeof(AdminContext))]
[Migration("20261024120000_VersionedSecretProtection")]
public sealed class VersionedSecretProtection : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Never invalidate a live durable approval during the breaking crypto upgrade.
        migrationBuilder.Sql("""
            CREATE TEMP TABLE "__SecretUpgradeGate" ("Count" INTEGER NOT NULL CHECK ("Count" = 0));
            INSERT INTO "__SecretUpgradeGate" SELECT COUNT(*) FROM "DmlApprovalRequests" WHERE "Status" IN ('Pending', 'Executing');
            DROP TABLE "__SecretUpgradeGate";
            ALTER TABLE "DbManagement" ADD COLUMN "SecretBindingId" TEXT NOT NULL DEFAULT '';
            UPDATE "DbManagement" SET "SecretBindingId" = lower(hex(randomblob(16)));
            CREATE UNIQUE INDEX "IX_DbManagement_SecretBindingId" ON "DbManagement" ("SecretBindingId");
            """);

    }
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Restore the pre-upgrade backup to roll back secret protection.");
}
