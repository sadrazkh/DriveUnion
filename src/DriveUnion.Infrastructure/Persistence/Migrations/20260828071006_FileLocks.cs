using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DriveUnion.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FileLocks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FileLocks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    StoredFileId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    PlaintextLength = table.Column<long>(type: "bigint", nullable: false),
                    BytesSealed = table.Column<long>(type: "bigint", nullable: false),
                    GoogleAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceDriveFileId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    SealedDriveFileId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    SourceRemoved = table.Column<bool>(type: "boolean", nullable: false),
                    KdfSalt = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    KdfIterations = table.Column<int>(type: "integer", nullable: false),
                    WrappedKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NoncePrefix = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    FailureReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileLocks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FileLocks_Status",
                table: "FileLocks",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_FileLocks_StoredFileId",
                table: "FileLocks",
                column: "StoredFileId");

            migrationBuilder.CreateIndex(
                name: "IX_FileLocks_TenantId_CreatedAt",
                table: "FileLocks",
                columns: new[] { "TenantId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FileLocks");
        }
    }
}
