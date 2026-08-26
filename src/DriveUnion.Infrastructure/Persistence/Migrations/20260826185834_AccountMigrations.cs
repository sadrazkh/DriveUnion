using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DriveUnion.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AccountMigrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccountMigrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    FilesMoved = table.Column<int>(type: "integer", nullable: false),
                    FilesFailed = table.Column<int>(type: "integer", nullable: false),
                    BytesMoved = table.Column<long>(type: "bigint", nullable: false),
                    FailureReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountMigrations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FileRelocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MigrationId = table.Column<Guid>(type: "uuid", nullable: false),
                    StoredFileId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceDriveFileId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    TargetAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetDriveFileId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    FailureReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    MovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileRelocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FileRelocations_AccountMigrations_MigrationId",
                        column: x => x.MigrationId,
                        principalTable: "AccountMigrations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountMigrations_SourceAccountId_Status",
                table: "AccountMigrations",
                columns: new[] { "SourceAccountId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_FileRelocations_MigrationId_Status",
                table: "FileRelocations",
                columns: new[] { "MigrationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_FileRelocations_Status_MovedAt",
                table: "FileRelocations",
                columns: new[] { "Status", "MovedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FileRelocations");

            migrationBuilder.DropTable(
                name: "AccountMigrations");
        }
    }
}
