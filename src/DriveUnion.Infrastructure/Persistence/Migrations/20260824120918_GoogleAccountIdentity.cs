using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DriveUnion.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GoogleAccountIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GoogleUserId",
                table: "GoogleAccounts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_GoogleAccounts_GoogleUserId",
                table: "GoogleAccounts",
                column: "GoogleUserId",
                unique: true,
                filter: "\"GoogleUserId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_GoogleAccounts_GoogleUserId",
                table: "GoogleAccounts");

            migrationBuilder.DropColumn(
                name: "GoogleUserId",
                table: "GoogleAccounts");
        }
    }
}
