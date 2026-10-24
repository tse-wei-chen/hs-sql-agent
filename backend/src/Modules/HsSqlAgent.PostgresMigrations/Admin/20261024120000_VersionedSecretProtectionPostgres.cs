using Admin.Service.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace HsSqlAgent.PostgresMigrations.Admin;

[DbContext(typeof(AdminContext))]
[Migration("20261024120000_VersionedSecretProtectionPostgres")]
public sealed class VersionedSecretProtectionPostgres : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Never invalidate a live durable approval during the breaking crypto upgrade.
        migrationBuilder.Sql("""
            CREATE TEMP TABLE "__SecretUpgradeGate" ("Count" INTEGER NOT NULL CHECK ("Count" = 0));
            INSERT INTO "__SecretUpgradeGate" SELECT COUNT(*) FROM "DmlApprovalRequests" WHERE "Status" IN ('Pending', 'Executing');
            DROP TABLE "__SecretUpgradeGate";
            ALTER TABLE "DbManagement" ADD COLUMN "SecretBindingId" character varying(32) NOT NULL DEFAULT '';
            UPDATE "DbManagement" SET "SecretBindingId" = lpad(to_hex("Id"), 32, '0');
            CREATE UNIQUE INDEX "IX_DbManagement_SecretBindingId" ON "DbManagement" ("SecretBindingId");
            """);
        migrationBuilder.Sql("ALTER TABLE \"DbManagement\" ALTER COLUMN \"PasswordHash\" TYPE text;");
    }
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Restore the pre-upgrade backup to roll back secret protection.");
}
