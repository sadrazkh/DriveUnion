using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DriveUnion.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TheMeterAndTheSender : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SenderUserId",
                table: "TelegramOutbox",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TenantUsageDays",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    EgressBytes = table.Column<long>(type: "bigint", nullable: false),
                    Downloads = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantUsageDays", x => new { x.TenantId, x.Day });
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantUsageDays_Day",
                table: "TenantUsageDays",
                column: "Day");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantUsageDays");

            migrationBuilder.DropColumn(
                name: "SenderUserId",
                table: "TelegramOutbox");
        }
    }
}
