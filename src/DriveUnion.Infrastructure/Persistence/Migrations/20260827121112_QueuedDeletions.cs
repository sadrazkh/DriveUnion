using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DriveUnion.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class QueuedDeletions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DeletionAttempts",
                table: "StoredFiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "PendingDeletionJobId",
                table: "StoredFiles",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DeletionJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Scope = table.Column<int>(type: "integer", nullable: false),
                    FolderName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    FilesTotal = table.Column<int>(type: "integer", nullable: false),
                    FilesMoved = table.Column<int>(type: "integer", nullable: false),
                    FilesFailed = table.Column<int>(type: "integer", nullable: false),
                    FailureReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeletionJobs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StoredFiles_PendingDeletionJobId",
                table: "StoredFiles",
                column: "PendingDeletionJobId",
                filter: "\"PendingDeletionJobId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DeletionJobs_Status",
                table: "DeletionJobs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_DeletionJobs_TenantId_CreatedAt",
                table: "DeletionJobs",
                columns: new[] { "TenantId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeletionJobs");

            migrationBuilder.DropIndex(
                name: "IX_StoredFiles_PendingDeletionJobId",
                table: "StoredFiles");

            migrationBuilder.DropColumn(
                name: "DeletionAttempts",
                table: "StoredFiles");

            migrationBuilder.DropColumn(
                name: "PendingDeletionJobId",
                table: "StoredFiles");
        }
    }
}
