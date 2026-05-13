using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Admin.Service.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDbSemantic : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DbSemantics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DbManagementId = table.Column<int>(type: "INTEGER", nullable: false),
                    SchemaName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    TableName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ColumnName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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

            migrationBuilder.CreateIndex(
                name: "IX_DbSemantics_DbManagementId_SchemaName_TableName_ColumnName",
                table: "DbSemantics",
                columns: new[] { "DbManagementId", "SchemaName", "TableName", "ColumnName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DbSemantics");
        }
    }
}
