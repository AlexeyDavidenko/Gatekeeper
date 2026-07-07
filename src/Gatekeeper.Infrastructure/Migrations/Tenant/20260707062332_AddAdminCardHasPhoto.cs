using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gatekeeper.Infrastructure.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class AddAdminCardHasPhoto : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AdminCardHasPhoto",
                table: "Applications",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdminCardHasPhoto",
                table: "Applications");
        }
    }
}
