using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HsSqlAgent.SqliteMigrations.Admin
{
    /// <inheritdoc />
    public partial class AddCustomToolLifecycle : Migration
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
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublishedIdentity",
                table: "CustomSqlTools",
                type: "TEXT",
                maxLength: 220,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PublishedRevisionId",
                table: "CustomSqlTools",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "CustomSqlTools",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Draft");

            // Legacy rows contain QueryDefinition/DmlDefinition JSON rather than SQL.
            migrationBuilder.Sql("UPDATE CustomSqlTools SET Status = 'Disabled';");

            migrationBuilder.CreateTable(
                name: "CustomSqlToolRevisions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CustomSqlToolId = table.Column<int>(type: "INTEGER", nullable: false),
                    RevisionNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    DbManagementId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    SqlTemplate = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ParametersJson = table.Column<string>(type: "TEXT", nullable: true),
                    DiffJson = table.Column<string>(type: "TEXT", nullable: false),
                    PublishedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    PublishedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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
