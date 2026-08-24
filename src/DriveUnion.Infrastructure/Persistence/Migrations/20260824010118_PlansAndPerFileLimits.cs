using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace DriveUnion.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PlansAndPerFileLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "MaxFileBytes",
                table: "Tenants",
                type: "bigint",
                nullable: false,
                defaultValue: 1073741824L);

            migrationBuilder.AddColumn<int>(
                name: "MaxMembers",
                table: "Tenants",
                type: "integer",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<long>(
                name: "MonthlyEgressBytes",
                table: "Tenants",
                type: "bigint",
                nullable: false,
                defaultValue: 322122547200L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PlanAppliedAt",
                table: "Tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PlanId",
                table: "Tenants",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "StorageQuotaBytes",
                table: "Tenants",
                type: "bigint",
                nullable: false,
                defaultValue: 107374182400L);

            migrationBuilder.AddColumn<long>(
                name: "StorageUsedBytes",
                table: "Tenants",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "Plans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    StorageBytes = table.Column<long>(type: "bigint", nullable: false),
                    MaxFileBytes = table.Column<long>(type: "bigint", nullable: false),
                    MonthlyEgressBytes = table.Column<long>(type: "bigint", nullable: false),
                    MaxMembers = table.Column<int>(type: "integer", nullable: false),
                    IsRetired = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Plans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantQuotaChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ChangedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    PlanCodeBefore = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    PlanCodeAfter = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Field = table.Column<byte>(type: "smallint", nullable: false),
                    OldValue = table.Column<long>(type: "bigint", nullable: false),
                    NewValue = table.Column<long>(type: "bigint", nullable: false),
                    Reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantQuotaChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantQuotaChanges_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "Plans",
                columns: new[] { "Id", "Code", "CreatedAt", "IsRetired", "MaxFileBytes", "MaxMembers", "MonthlyEgressBytes", "Name", "SortOrder", "StorageBytes" },
                values: new object[,]
                {
                    { new Guid("10000000-0000-4000-8000-000000000001"), "starter", new DateTimeOffset(new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), false, 1073741824L, 3, 322122547200L, "پایه", 10, 107374182400L },
                    { new Guid("10000000-0000-4000-8000-000000000002"), "standard", new DateTimeOffset(new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), false, 2147483648L, 10, 1649267441664L, "استاندارد", 20, 536870912000L },
                    { new Guid("10000000-0000-4000-8000-000000000003"), "business", new DateTimeOffset(new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), false, 8589934592L, 25, 6597069766656L, "تجاری", 30, 2199023255552L }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_PlanId",
                table: "Tenants",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_Plans_Code",
                table: "Plans",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantQuotaChanges_TenantId_ChangedAt",
                table: "TenantQuotaChanges",
                columns: new[] { "TenantId", "ChangedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_Tenants_Plans_PlanId",
                table: "Tenants",
                column: "PlanId",
                principalTable: "Plans",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Tenants_Plans_PlanId",
                table: "Tenants");

            migrationBuilder.DropTable(
                name: "Plans");

            migrationBuilder.DropTable(
                name: "TenantQuotaChanges");

            migrationBuilder.DropIndex(
                name: "IX_Tenants_PlanId",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "MaxFileBytes",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "MaxMembers",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "MonthlyEgressBytes",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "PlanAppliedAt",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "PlanId",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "StorageQuotaBytes",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "StorageUsedBytes",
                table: "Tenants");
        }
    }
}
