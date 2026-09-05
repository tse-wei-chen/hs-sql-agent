using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HsSqlAgent.SqliteMigrations.Admin
{
    /// <inheritdoc />
    public partial class AddDurableDmlApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DmlApprovalRequests",
                columns: table => new
                {
                    RequestId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ApprovalFingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    EvidenceFingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    ProtectedExecutionPayload = table.Column<string>(type: "TEXT", nullable: false),
                    RequesterIdentity = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    TargetIdentity = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    DatabaseProvider = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    DatabaseIdentity = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AccessKeyId = table.Column<int>(type: "INTEGER", nullable: false),
                    DbManagementId = table.Column<int>(type: "INTEGER", nullable: false),
                    RequiredToolName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CustomToolId = table.Column<int>(type: "INTEGER", nullable: true),
                    CustomToolRevisionId = table.Column<int>(type: "INTEGER", nullable: true),
                    StatementCount = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalAffectedRows = table.Column<int>(type: "INTEGER", nullable: false),
                    ExternalReference = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ApproverIdentity = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DmlApprovalRequests", x => x.RequestId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DmlApprovalRequests_ExternalReference",
                table: "DmlApprovalRequests",
                column: "ExternalReference");

            migrationBuilder.CreateIndex(
                name: "IX_DmlApprovalRequests_Status_ExpiresAt",
                table: "DmlApprovalRequests",
                columns: new[] { "Status", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DmlApprovalRequests");
        }
    }
}
