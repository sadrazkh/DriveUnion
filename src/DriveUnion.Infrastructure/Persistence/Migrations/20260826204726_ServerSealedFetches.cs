using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DriveUnion.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ServerSealedFetches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SealedBy",
                table: "UploadSessions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "KdfIterations",
                table: "RemoteFetches",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "KdfSalt",
                table: "RemoteFetches",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NoncePrefix",
                table: "RemoteFetches",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WrappedKey",
                table: "RemoteFetches",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SealedBy",
                table: "FileEncryptions",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SealedBy",
                table: "UploadSessions");

            migrationBuilder.DropColumn(
                name: "KdfIterations",
                table: "RemoteFetches");

            migrationBuilder.DropColumn(
                name: "KdfSalt",
                table: "RemoteFetches");

            migrationBuilder.DropColumn(
                name: "NoncePrefix",
                table: "RemoteFetches");

            migrationBuilder.DropColumn(
                name: "WrappedKey",
                table: "RemoteFetches");

            migrationBuilder.DropColumn(
                name: "SealedBy",
                table: "FileEncryptions");
        }
    }
}
