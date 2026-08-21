using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HsSqlAgent.SqliteMigrations.Admin
{
    /// <inheritdoc />
    public partial class UpdateEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PermitLimitOverride",
                table: "McpAccessKeys");

            migrationBuilder.DropColumn(
                name: "QueueLimitOverride",
                table: "McpAccessKeys");

            migrationBuilder.DropColumn(
                name: "WindowSecondsOverride",
                table: "McpAccessKeys");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PermitLimitOverride",
                table: "McpAccessKeys",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QueueLimitOverride",
                table: "McpAccessKeys",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WindowSecondsOverride",
                table: "McpAccessKeys",
                type: "INTEGER",
                nullable: true);
        }
    }
}
