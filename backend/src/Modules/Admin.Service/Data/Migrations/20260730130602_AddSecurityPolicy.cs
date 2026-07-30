using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Admin.Service.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSecurityPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SecurityPolicySettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    QueryMaxRows = table.Column<int>(type: "INTEGER", nullable: false),
                    QueryTimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    RequireWhereForUpdate = table.Column<bool>(type: "INTEGER", nullable: false),
                    RequireWhereForDelete = table.Column<bool>(type: "INTEGER", nullable: false),
                    AllowFullTableUpdate = table.Column<bool>(type: "INTEGER", nullable: false),
                    AllowFullTableDelete = table.Column<bool>(type: "INTEGER", nullable: false),
                    DmlMaxAffectedRows = table.Column<int>(type: "INTEGER", nullable: false),
                    IpPermitLimit = table.Column<int>(type: "INTEGER", nullable: false),
                    IpWindowSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    KeyPermitLimit = table.Column<int>(type: "INTEGER", nullable: false),
                    KeyWindowSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxConcurrentSql = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityPolicySettings", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "SecurityPolicySettings",
                columns: new[] { "Id", "AllowFullTableDelete", "AllowFullTableUpdate", "DmlMaxAffectedRows", "IpPermitLimit", "IpWindowSeconds", "KeyPermitLimit", "KeyWindowSeconds", "MaxConcurrentSql", "QueryMaxRows", "QueryTimeoutSeconds", "RequireWhereForDelete", "RequireWhereForUpdate", "UpdatedAt", "UpdatedBy" },
                values: new object[] { 1, false, false, 100, 60, 60, 120, 60, 16, 1000, 30, true, true, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SecurityPolicySettings");
        }
    }
}
