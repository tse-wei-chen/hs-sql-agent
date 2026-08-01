using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HsSqlAgent.PostgresMigrations.Admin
{
    /// <inheritdoc />
    public partial class InitialAdminPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ActorId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Action = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Target = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Detail = table.Column<string>(type: "text", nullable: true),
                    Result = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    RequestId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    SessionId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    AccessKeyId = table.Column<int>(type: "integer", nullable: true),
                    DbManagementId = table.Column<int>(type: "integer", nullable: true),
                    DatabaseName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ToolName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Operation = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: true),
                    ReturnedRows = table.Column<int>(type: "integer", nullable: true),
                    AffectedRows = table.Column<int>(type: "integer", nullable: true),
                    ApprovalStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ErrorCategory = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Definition = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CustomSqlTools",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    DefinitionJson = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ParametersJson = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastModifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomSqlTools", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DbManagement",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SqlProvider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Host = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Port = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    Username = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PasswordHash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Database = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ExtraSettings = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DbManagement", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "McpAccessKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    KeyPrefix = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    KeyHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AllowedTools = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    CorsAllowedOrigins = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    SqlProvider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    DbManagementId = table.Column<int>(type: "integer", nullable: true),
                    TableWhitelist = table.Column<string>(type: "text", nullable: true),
                    RateLimitMode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "Inherit"),
                    PermitLimitOverride = table.Column<int>(type: "integer", nullable: true),
                    WindowSecondsOverride = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpAccessKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SecurityPolicySettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    QueryMaxRows = table.Column<int>(type: "integer", nullable: false),
                    QueryTimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                    RequireWhereForUpdate = table.Column<bool>(type: "boolean", nullable: false),
                    RequireWhereForDelete = table.Column<bool>(type: "boolean", nullable: false),
                    AllowFullTableUpdate = table.Column<bool>(type: "boolean", nullable: false),
                    AllowFullTableDelete = table.Column<bool>(type: "boolean", nullable: false),
                    DmlMaxAffectedRows = table.Column<int>(type: "integer", nullable: false),
                    KeyPermitLimit = table.Column<int>(type: "integer", nullable: false),
                    KeyWindowSeconds = table.Column<int>(type: "integer", nullable: false),
                    MaxConcurrentSql = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityPolicySettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DbSemantics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DbManagementId = table.Column<int>(type: "integer", nullable: false),
                    SchemaName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    TableName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ColumnName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DbSemantics", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DbSemantics_DbManagement_DbManagementId",
                        column: x => x.DbManagementId,
                        principalTable: "DbManagement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "SecurityPolicySettings",
                columns: new[] { "Id", "AllowFullTableDelete", "AllowFullTableUpdate", "DmlMaxAffectedRows", "KeyPermitLimit", "KeyWindowSeconds", "MaxConcurrentSql", "QueryMaxRows", "QueryTimeoutSeconds", "RequireWhereForDelete", "RequireWhereForUpdate", "UpdatedAt", "UpdatedBy" },
                values: new object[] { 1, false, false, 100, 120, 60, 16, 1000, 30, true, true, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_AccessKeyId",
                table: "AuditLogs",
                column: "AccessKeyId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Action",
                table: "AuditLogs",
                column: "Action");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_CreatedAt",
                table: "AuditLogs",
                column: "CreatedAt");

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

            migrationBuilder.CreateIndex(
                name: "IX_DbSemantics_DbManagementId_SchemaName_TableName_ColumnName",
                table: "DbSemantics",
                columns: new[] { "DbManagementId", "SchemaName", "TableName", "ColumnName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpAccessKeys_IsActive",
                table: "McpAccessKeys",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_McpAccessKeys_KeyHash",
                table: "McpAccessKeys",
                column: "KeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpAccessKeys_KeyPrefix",
                table: "McpAccessKeys",
                column: "KeyPrefix");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "CustomSqlTools");

            migrationBuilder.DropTable(
                name: "DbSemantics");

            migrationBuilder.DropTable(
                name: "McpAccessKeys");

            migrationBuilder.DropTable(
                name: "SecurityPolicySettings");

            migrationBuilder.DropTable(
                name: "DbManagement");
        }
    }
}
