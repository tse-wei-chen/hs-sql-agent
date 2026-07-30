using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Admin.Service.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddP1Governance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PermitLimitOverride",
                table: "McpAccessKeys",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RateLimitMode",
                table: "McpAccessKeys",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "Inherit");

            migrationBuilder.AddColumn<int>(
                name: "WindowSecondsOverride",
                table: "McpAccessKeys",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AccessKeyId",
                table: "AuditLogs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AffectedRows",
                table: "AuditLogs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApprovalStatus",
                table: "AuditLogs",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DatabaseName",
                table: "AuditLogs",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DbManagementId",
                table: "AuditLogs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Definition",
                table: "AuditLogs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DurationMs",
                table: "AuditLogs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErrorCategory",
                table: "AuditLogs",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EventId",
                table: "AuditLogs",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "Operation",
                table: "AuditLogs",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestId",
                table: "AuditLogs",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReturnedRows",
                table: "AuditLogs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SessionId",
                table: "AuditLogs",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ToolName",
                table: "AuditLogs",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SecurityPolicySettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    QueryMaxRows = table.Column<int>(type: "INTEGER", nullable: false),
                    QueryTimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    RequireWhereForUpdate = table.Column<bool>(type: "INTEGER", nullable: false),
                    RequireWhereForDelete = table.Column<bool>(type: "INTEGER", nullable: false),
                    AllowFullTableUpdate = table.Column<bool>(type: "INTEGER", nullable: false),
                    AllowFullTableDelete = table.Column<bool>(type: "INTEGER", nullable: false),
                    DmlMaxAffectedRows = table.Column<int>(type: "INTEGER", nullable: false),
                    KeyPermitLimit = table.Column<int>(type: "INTEGER", nullable: false),
                    KeyWindowSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxConcurrentSql = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityPolicySettings", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "SecurityPolicySettings",
                columns: new[] { "Id", "AllowFullTableDelete", "AllowFullTableUpdate", "DmlMaxAffectedRows", "KeyPermitLimit", "KeyWindowSeconds", "MaxConcurrentSql", "QueryMaxRows", "QueryTimeoutSeconds", "RequireWhereForDelete", "RequireWhereForUpdate", "UpdatedAt", "UpdatedBy" },
                values: new object[] { 1, false, false, 100, 120, 60, 16, 1000, 30, true, true, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null });

            migrationBuilder.Sql(
                """
                UPDATE AuditLogs
                SET EventId =
                    lower(hex(randomblob(4))) || '-' ||
                    lower(hex(randomblob(2))) || '-' ||
                    lower(hex(randomblob(2))) || '-' ||
                    lower(hex(randomblob(2))) || '-' ||
                    lower(hex(randomblob(6)));
                """);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_AccessKeyId",
                table: "AuditLogs",
                column: "AccessKeyId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_DbManagementId",
                table: "AuditLogs",
                column: "DbManagementId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_EventId",
                table: "AuditLogs",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_ToolName",
                table: "AuditLogs",
                column: "ToolName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SecurityPolicySettings");

            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_AccessKeyId",
                table: "AuditLogs");

            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_DbManagementId",
                table: "AuditLogs");

            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_EventId",
                table: "AuditLogs");

            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_ToolName",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "PermitLimitOverride",
                table: "McpAccessKeys");

            migrationBuilder.DropColumn(
                name: "RateLimitMode",
                table: "McpAccessKeys");

            migrationBuilder.DropColumn(
                name: "WindowSecondsOverride",
                table: "McpAccessKeys");

            migrationBuilder.DropColumn(
                name: "AccessKeyId",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "AffectedRows",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "ApprovalStatus",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "DatabaseName",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "DbManagementId",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "Definition",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "DurationMs",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "ErrorCategory",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "EventId",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "Operation",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "RequestId",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "ReturnedRows",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "SessionId",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "ToolName",
                table: "AuditLogs");
        }
    }
}
