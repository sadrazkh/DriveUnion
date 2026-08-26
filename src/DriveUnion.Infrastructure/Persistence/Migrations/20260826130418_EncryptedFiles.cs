using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DriveUnion.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EncryptedFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EncryptionHeaderJson",
                table: "UploadSessions",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FileEncryptions",
                columns: table => new
                {
                    StoredFileId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Scheme = table.Column<int>(type: "integer", nullable: false),
                    SegmentSize = table.Column<int>(type: "integer", nullable: false),
                    NoncePrefix = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PlaintextLength = table.Column<long>(type: "bigint", nullable: false),
                    KdfSalt = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    KdfIterations = table.Column<int>(type: "integer", nullable: false),
                    WrappedKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileEncryptions", x => x.StoredFileId);
                    table.ForeignKey(
                        name: "FK_FileEncryptions_StoredFiles_StoredFileId",
                        column: x => x.StoredFileId,
                        principalTable: "StoredFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FileEncryptions_TenantId",
                table: "FileEncryptions",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FileEncryptions");

            migrationBuilder.DropColumn(
                name: "EncryptionHeaderJson",
                table: "UploadSessions");
        }
    }
}
