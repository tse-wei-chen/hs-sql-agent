using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HsSqlAgent.PostgresMigrations.Admin
{
    /// <inheritdoc />
    public partial class AddOperabilityPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DbHealthStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DbManagementId = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    LastCheckedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSuccessAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LatencyMs = table.Column<long>(type: "bigint", nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    OutageStartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
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
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DedupeKey = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    TargetUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeliveredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboundDeliveries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RateLimitMetrics",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BucketStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Layer = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AccessKeyId = table.Column<int>(type: "integer", nullable: true),
                    DbManagementId = table.Column<int>(type: "integer", nullable: true),
                    ToolName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    RejectedCount = table.Column<long>(type: "bigint", nullable: false)
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
