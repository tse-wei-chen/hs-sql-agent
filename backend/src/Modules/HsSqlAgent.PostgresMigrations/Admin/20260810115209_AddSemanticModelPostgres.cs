using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HsSqlAgent.PostgresMigrations.Admin
{
    /// <inheritdoc />
    public partial class AddSemanticModelPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SynonymsJson",
                table: "DbSemantics",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DbSemanticMetrics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DbManagementId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Formula = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Aggregation = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Grain = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Filter = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    SynonymsJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DbSemanticMetrics", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DbSemanticMetrics_DbManagement_DbManagementId",
                        column: x => x.DbManagementId,
                        principalTable: "DbManagement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DbSemanticRelationships",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DbManagementId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SourceSchema = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    SourceTable = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SourceColumn = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TargetSchema = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    TargetTable = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TargetColumn = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Cardinality = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Direction = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DbSemanticRelationships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DbSemanticRelationships_DbManagement_DbManagementId",
                        column: x => x.DbManagementId,
                        principalTable: "DbManagement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DbSemanticMetrics_DbManagementId_Name",
                table: "DbSemanticMetrics",
                columns: new[] { "DbManagementId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DbSemanticRelationships_DbManagementId_Name",
                table: "DbSemanticRelationships",
                columns: new[] { "DbManagementId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DbSemanticMetrics");

            migrationBuilder.DropTable(
                name: "DbSemanticRelationships");

            migrationBuilder.DropColumn(
                name: "SynonymsJson",
                table: "DbSemantics");
        }
    }
}
