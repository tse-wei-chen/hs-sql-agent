using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Admin.Service.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMetricScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DbSemanticMetrics_DbManagementId_Name",
                table: "DbSemanticMetrics");

            migrationBuilder.AddColumn<string>(
                name: "SchemaName",
                table: "DbSemanticMetrics",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TableName",
                table: "DbSemanticMetrics",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_DbSemanticMetrics_DbManagementId_SchemaName_TableName_Name",
                table: "DbSemanticMetrics",
                columns: new[] { "DbManagementId", "SchemaName", "TableName", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DbSemanticMetrics_DbManagementId_SchemaName_TableName_Name",
                table: "DbSemanticMetrics");

            migrationBuilder.DropColumn(
                name: "SchemaName",
                table: "DbSemanticMetrics");

            migrationBuilder.DropColumn(
                name: "TableName",
                table: "DbSemanticMetrics");

            migrationBuilder.CreateIndex(
                name: "IX_DbSemanticMetrics_DbManagementId_Name",
                table: "DbSemanticMetrics",
                columns: new[] { "DbManagementId", "Name" },
                unique: true);
        }
    }
}
