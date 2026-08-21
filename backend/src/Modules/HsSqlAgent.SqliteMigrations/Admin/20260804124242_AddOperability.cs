using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HsSqlAgent.SqliteMigrations.Admin
{
    /// <inheritdoc />
    public partial class AddOperability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DbHealthStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DbManagementId = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    LastCheckedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSuccessAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LatencyMs = table.Column<long>(type: "INTEGER", nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    OutageStartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DbHealthStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DbHealthStates_DbManagement_DbManagementId",
                        column: x => x.DbManagementId,
                        principalTable: "DbManagement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OutboundDeliveries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Category = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    DedupeKey = table.Column<string>(type: "TEXT", maxLength: 250, nullable: false),
                    TargetUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeliveredAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboundDeliveries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RateLimitMetrics",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BucketStart = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Layer = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AccessKeyId = table.Column<int>(type: "INTEGER", nullable: true),
                    DbManagementId = table.Column<int>(type: "INTEGER", nullable: true),
                    ToolName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    RejectedCount = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RateLimitMetrics", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DbHealthStates_DbManagementId",
                table: "DbHealthStates",
                column: "DbManagementId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboundDeliveries_DedupeKey",
                table: "OutboundDeliveries",
                column: "DedupeKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboundDeliveries_Status_NextAttemptAt",
                table: "OutboundDeliveries",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RateLimitMetrics_AccessKeyId",
                table: "RateLimitMetrics",
                column: "AccessKeyId");

            migrationBuilder.CreateIndex(
                name: "IX_RateLimitMetrics_BucketStart",
                table: "RateLimitMetrics",
                column: "BucketStart");

            migrationBuilder.CreateIndex(
                name: "IX_RateLimitMetrics_DbManagementId",
                table: "RateLimitMetrics",
                column: "DbManagementId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DbHealthStates");

            migrationBuilder.DropTable(
                name: "OutboundDeliveries");

            migrationBuilder.DropTable(
                name: "RateLimitMetrics");
        }
    }
}
