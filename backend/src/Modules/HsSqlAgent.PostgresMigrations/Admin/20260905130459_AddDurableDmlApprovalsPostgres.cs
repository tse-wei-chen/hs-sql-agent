using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HsSqlAgent.PostgresMigrations.Admin
{
    /// <inheritdoc />
    public partial class AddDurableDmlApprovalsPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DmlApprovalRequests",
                columns: table => new
                {
                    RequestId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ApprovalFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EvidenceFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    ProtectedExecutionPayload = table.Column<string>(type: "text", nullable: false),
                    RequesterIdentity = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TargetIdentity = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DatabaseProvider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DatabaseIdentity = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AccessKeyId = table.Column<int>(type: "integer", nullable: false),
                    DbManagementId = table.Column<int>(type: "integer", nullable: false),
                    RequiredToolName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CustomToolId = table.Column<int>(type: "integer", nullable: true),
                    CustomToolRevisionId = table.Column<int>(type: "integer", nullable: true),
                    StatementCount = table.Column<int>(type: "integer", nullable: false),
                    TotalAffectedRows = table.Column<int>(type: "integer", nullable: false),
                    ExternalReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ApproverIdentity = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
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
