using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DriveUnion.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TrashAndPerUserFolders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DriveFolderId",
                table: "StoredFiles",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerUserId",
                table: "StoredFiles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PurgeAfter",
                table: "StoredFiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RestoreFolderId",
                table: "StoredFiles",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OperatorSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    TrashRetentionDays = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperatorSettings", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "OperatorSettings",
                columns: new[] { "Id", "TrashRetentionDays", "UpdatedAt", "UpdatedByUserId" },
                values: new object[] { 1, 30, null, null });

            migrationBuilder.CreateIndex(
                name: "IX_StoredFiles_PurgeAfter",
                table: "StoredFiles",
                column: "PurgeAfter",
                filter: "\"PurgeAfter\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OperatorSettings");

            migrationBuilder.DropIndex(
                name: "IX_StoredFiles_PurgeAfter",
                table: "StoredFiles");

            migrationBuilder.DropColumn(
                name: "DriveFolderId",
                table: "StoredFiles");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "StoredFiles");

            migrationBuilder.DropColumn(
                name: "PurgeAfter",
                table: "StoredFiles");

            migrationBuilder.DropColumn(
                name: "RestoreFolderId",
                table: "StoredFiles");
        }
    }
}
