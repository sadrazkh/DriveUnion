using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DriveUnion.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CatalogueSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CatalogueSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ByHand = table.Column<bool>(type: "boolean", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    TenantCount = table.Column<int>(type: "integer", nullable: false),
                    AccountCount = table.Column<int>(type: "integer", nullable: false),
                    FolderCount = table.Column<int>(type: "integer", nullable: false),
                    FileCount = table.Column<int>(type: "integer", nullable: false),
                    EncryptionCount = table.Column<int>(type: "integer", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    CopiesWanted = table.Column<int>(type: "integer", nullable: false),
                    CopiesMade = table.Column<int>(type: "integer", nullable: false),
                    FailureReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogueSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CatalogueSnapshotCopies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    GoogleAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    DriveFileId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DriveFolderId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    WrittenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RemovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogueSnapshotCopies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CatalogueSnapshotCopies_CatalogueSnapshots_SnapshotId",
                        column: x => x.SnapshotId,
                        principalTable: "CatalogueSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogueSnapshotCopies_GoogleAccountId_RemovedAt",
                table: "CatalogueSnapshotCopies",
                columns: new[] { "GoogleAccountId", "RemovedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogueSnapshotCopies_SnapshotId",
                table: "CatalogueSnapshotCopies",
                column: "SnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogueSnapshots_Status_RequestedAt",
                table: "CatalogueSnapshots",
                columns: new[] { "Status", "RequestedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CatalogueSnapshotCopies");

            migrationBuilder.DropTable(
                name: "CatalogueSnapshots");
        }
    }
}
