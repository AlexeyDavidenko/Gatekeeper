using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gatekeeper.Infrastructure.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class ArchiveModerationActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ArchivedAt",
                table: "ModerationActions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsArchived",
                table: "ModerationActions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_ModerationActions_IsArchived_CreatedAt",
                table: "ModerationActions",
                columns: new[] { "IsArchived", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ModerationActions_IsArchived_CreatedAt",
                table: "ModerationActions");

            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                table: "ModerationActions");

            migrationBuilder.DropColumn(
                name: "IsArchived",
                table: "ModerationActions");
        }
    }
}
