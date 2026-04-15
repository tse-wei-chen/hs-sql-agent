using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Admin.Service.Data.Migrations
{
	/// <inheritdoc />
	public partial class AddCorsSetting : Migration
	{
		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.AddColumn<string>(
				name: "CorsAllowedOrigins",
				table: "McpAccessKeys",
				type: "TEXT",
				maxLength: 4000,
				nullable: true);
		}

		/// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.DropColumn(
				name: "CorsAllowedOrigins",
				table: "McpAccessKeys");
		}
	}
}
