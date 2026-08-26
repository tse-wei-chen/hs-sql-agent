using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HsSqlAgent.PostgresMigrations.Admin
{
    /// <inheritdoc />
    public partial class AddCustomToolLifecyclePostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "DefinitionJson",
                table: "CustomSqlTools",
                newName: "SqlTemplate");

            migrationBuilder.AddColumn<int>(
                name: "DbManagementId",
                table: "CustomSqlTools",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublishedIdentity",
                table: "CustomSqlTools",
                type: "character varying(220)",
                maxLength: 220,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PublishedRevisionId",
                table: "CustomSqlTools",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "CustomSqlTools",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Draft");

            // Legacy rows contain QueryDefinition/DmlDefinition JSON rather than SQL.
            migrationBuilder.Sql("UPDATE \"CustomSqlTools\" SET \"Status\" = 'Disabled';");

            migrationBuilder.CreateTable(
                name: "CustomSqlToolRevisions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CustomSqlToolId = table.Column<int>(type: "integer", nullable: false),
                    RevisionNumber = table.Column<int>(type: "integer", nullable: false),
                    DbManagementId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SqlTemplate = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ParametersJson = table.Column<string>(type: "text", nullable: true),
                    DiffJson = table.Column<string>(type: "text", nullable: false),
                    PublishedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PublishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomSqlToolRevisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomSqlToolRevisions_CustomSqlTools_CustomSqlToolId",
                        column: x => x.CustomSqlToolId,
                        principalTable: "CustomSqlTools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CustomSqlToolRevisions_DbManagement_DbManagementId",
                        column: x => x.DbManagementId,
                        principalTable: "DbManagement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomSqlTools_DbManagementId",
                table: "CustomSqlTools",
                column: "DbManagementId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomSqlTools_PublishedIdentity",
                table: "CustomSqlTools",
                column: "PublishedIdentity",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomSqlTools_PublishedRevisionId",
                table: "CustomSqlTools",
                column: "PublishedRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomSqlToolRevisions_CustomSqlToolId_RevisionNumber",
                table: "CustomSqlToolRevisions",
                columns: new[] { "CustomSqlToolId", "RevisionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomSqlToolRevisions_DbManagementId",
                table: "CustomSqlToolRevisions",
                column: "DbManagementId");

            migrationBuilder.AddForeignKey(
                name: "FK_CustomSqlTools_CustomSqlToolRevisions_PublishedRevisionId",
                table: "CustomSqlTools",
                column: "PublishedRevisionId",
                principalTable: "CustomSqlToolRevisions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CustomSqlTools_DbManagement_DbManagementId",
                table: "CustomSqlTools",
                column: "DbManagementId",
                principalTable: "DbManagement",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CustomSqlTools_CustomSqlToolRevisions_PublishedRevisionId",
                table: "CustomSqlTools");

            migrationBuilder.DropForeignKey(
                name: "FK_CustomSqlTools_DbManagement_DbManagementId",
                table: "CustomSqlTools");

            migrationBuilder.DropTable(
                name: "CustomSqlToolRevisions");

            migrationBuilder.DropIndex(
                name: "IX_CustomSqlTools_DbManagementId",
                table: "CustomSqlTools");

            migrationBuilder.DropIndex(
                name: "IX_CustomSqlTools_PublishedIdentity",
                table: "CustomSqlTools");

            migrationBuilder.DropIndex(
                name: "IX_CustomSqlTools_PublishedRevisionId",
                table: "CustomSqlTools");

            migrationBuilder.DropColumn(
                name: "DbManagementId",
                table: "CustomSqlTools");

            migrationBuilder.DropColumn(
                name: "PublishedIdentity",
                table: "CustomSqlTools");

            migrationBuilder.DropColumn(
                name: "PublishedRevisionId",
                table: "CustomSqlTools");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "CustomSqlTools");

            migrationBuilder.RenameColumn(
                name: "SqlTemplate",
                table: "CustomSqlTools",
                newName: "DefinitionJson");
        }
    }
}
