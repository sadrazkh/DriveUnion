using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DriveUnion.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AbuseReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PublicSuspendedAt",
                table: "Tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublicSuspendedReason",
                table: "Tenants",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AbuseReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShareLinkId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ReporterEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ReporterIpHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Resolution = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ResolvedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AbuseReports", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AbuseReports_ShareLinkId_Status",
                table: "AbuseReports",
                columns: new[] { "ShareLinkId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AbuseReports_Status",
                table: "AbuseReports",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_AbuseReports_TenantId_Status",
                table: "AbuseReports",
                columns: new[] { "TenantId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AbuseReports");

            migrationBuilder.DropColumn(
                name: "PublicSuspendedAt",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "PublicSuspendedReason",
                table: "Tenants");
        }
    }
}
