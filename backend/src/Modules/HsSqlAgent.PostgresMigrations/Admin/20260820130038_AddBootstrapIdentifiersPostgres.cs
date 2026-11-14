using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HsSqlAgent.PostgresMigrations.Admin
{
    /// <inheritdoc />
    public partial class AddBootstrapIdentifiersPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BootstrapId",
                table: "McpAccessKeys",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BootstrapId",
                table: "DbManagement",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpAccessKeys_BootstrapId",
                table: "McpAccessKeys",
                column: "BootstrapId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DbManagement_BootstrapId",
                table: "DbManagement",
                column: "BootstrapId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_McpAccessKeys_BootstrapId",
                table: "McpAccessKeys");

            migrationBuilder.DropIndex(
                name: "IX_DbManagement_BootstrapId",
                table: "DbManagement");

            migrationBuilder.DropColumn(
                name: "BootstrapId",
                table: "McpAccessKeys");

            migrationBuilder.DropColumn(
                name: "BootstrapId",
                table: "DbManagement");
        }
    }
}
