using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Admin.Service.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpKeyRateLimitPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PermitLimitOverride",
                table: "McpAccessKeys",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RateLimitMode",
                table: "McpAccessKeys",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "Inherit");

            migrationBuilder.AddColumn<int>(
                name: "WindowSecondsOverride",
                table: "McpAccessKeys",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PermitLimitOverride",
                table: "McpAccessKeys");

            migrationBuilder.DropColumn(
                name: "RateLimitMode",
                table: "McpAccessKeys");

            migrationBuilder.DropColumn(
                name: "WindowSecondsOverride",
                table: "McpAccessKeys");
        }
    }
}
