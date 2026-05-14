using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Admin.Service.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDbManagementIdToMcpAccessKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SqlConnectionString",
                table: "McpAccessKeys");

            migrationBuilder.AddColumn<int>(
                name: "DbManagementId",
                table: "McpAccessKeys",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DbManagementId",
                table: "McpAccessKeys");

            migrationBuilder.AddColumn<string>(
                name: "SqlConnectionString",
                table: "McpAccessKeys",
                type: "TEXT",
                maxLength: 4000,
                nullable: true);
        }
    }
}
