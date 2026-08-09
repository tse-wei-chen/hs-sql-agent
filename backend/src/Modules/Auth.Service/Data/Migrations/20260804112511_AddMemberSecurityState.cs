using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auth.Service.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMemberSecurityState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "Members",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "SecurityVersion",
                table: "Members",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "Members");

            migrationBuilder.DropColumn(
                name: "SecurityVersion",
                table: "Members");
        }
    }
}
